using Sandbox.Rendering;

namespace Sandbox;

/// <summary>
/// Screen space ambient occlusion (SSAO)
/// </summary>
[Title( "SSAO" )]
[Category( "Post Processing" )]
[Icon( "contrast" )]
[Alias( "GroundTruthAmbientOcclusion" )]
public sealed class SSAO : PostProcess, Component.ExecuteInEditor
{
	public enum SampleQuality
	{
		/// <summary>
		/// 9 samples
		/// </summary>
		[Icon( "workspaces" )]
		Low,
		/// <summary>
		/// 16 samples
		/// </summary>
		[Icon( "grain" )]
		Medium,
		/// <summary>
		/// 25 samples
		/// </summary>
		[Icon( "blur_on" )]
		High
	}

	/// <summary>
	/// The intensity of the darkening effect. Has no impact on performance.
	/// </summary>
	[Property, Range( 0, 1 ), Category( "Properties" )] public float Intensity { get; set; } = 0.15f;

	/// <summary>
	/// Maximum distance of samples from pixel when determining its occlusion, in world units.
	/// </summary>
	[Property, Range( 1, 512 ), Category( "Properties" )] public int Radius { get; set; } = 128;

	/// <summary>
	/// Gently reduce sample impact as it gets out of the effect's radius bounds
	/// </summary>
	[Property, Range( 0.01f, 1.0f ), Category( "Properties" )] public float FalloffRange { get; set; } = 0.75f;

	/// <summary>
	/// Number of pixel samples taken to determine occlusion
	/// </summary>
	[Property, Category( "Quality" )] public SampleQuality Quality { get; set; } = SampleQuality.High;

	/// <summary>
	/// How we should denoise the effect
	/// </summary>
	[Property, Category( "Quality" )] public DenoiseModes DenoiseMode { get; set; } = DenoiseModes.Temporal;

	/// <summary>
	/// Slightly reduce impact of samples further back to counter the bias from depth-based (incomplete) input scene geometry data
	/// </summary>
	[Property, Category( "Quality" ), Range( 0.0f, 0.5f )] public float ThinCompensation { get; set; } = 0.0f;


	Rendering.CommandList commands;

	protected override void OnEnabled()
	{
		commands = new Rendering.CommandList( "AmbientOcclusion" );
		OnDirty();

		Camera.AddCommandList( commands, Rendering.Stage.AfterDepthPrepass );
	}

	protected override void OnDisabled()
	{
		Camera.RemoveCommandList( commands );
		commands = null;
	}
	int Frame = 0;

	private struct GTAOConstants
	{
		public Vector2Int ViewportSize; // Unused with Command Lists
		public Vector2 ViewportPixelSize;                  // Unused with Command Lists

		public Vector2 DepthUnpackConsts;
		public Vector2 CameraTanHalfFOV;

		public Vector2 NDCToViewMul;
		public Vector2 NDCToViewAdd;

		public Vector2 NDCToViewMul_x_PixelSize;
		public float EffectRadius;                       // world (viewspace) maximum size of the shadow
		public float EffectFalloffRange;

		public float RadiusMultiplier = 1.457f;
		public float TAABlendAmount = 0;
		public float FinalValuePower = 2.2f;             // modifies the final ambient occlusion value using power function - this allows some of the above heuristics to do different things
		public float DenoiseBlurBeta = 1.5f;
		public float SampleDistributionPower = 2.0f;      // small crevices more important than big surfaces
		public float ThinOccluderCompensation = 0.0f;    // the new 'thickness heuristic' approach
		public float DepthMIPSamplingOffset = 3.30f;     // main trade-off between performance (memory bandwidth) and quality (temporal stability is the first affected, thin objects next)
		public int NoiseIndex = 0;            // frameIndex % 64 if using TAA or 0 otherwise
		public GTAOConstants() { }
	};

	enum GTAOPasses
	{
		ViewDepthChain,    // XeGTAO depth filter does average depth, a bit similar to our depth chain
		MainPass,
		DenoiseSpatial,
		DenoiseTemporal
	}

	GTAOConstants GetGTAOConstants( /*SceneCamera camera*/ )
	{
		var consts = new GTAOConstants();

		// The above is calculated on shader now
		consts.ViewportSize = Vector2Int.Zero;
		consts.ViewportPixelSize = Vector2.Zero;
		consts.DepthUnpackConsts = Vector2.Zero;
		consts.CameraTanHalfFOV = Vector2.Zero;
		consts.NDCToViewMul = Vector2.Zero;
		consts.NDCToViewAdd = Vector2.Zero;

		consts.NDCToViewMul_x_PixelSize = Vector2.Zero;

		//-------------------------------------------------------------------------
		consts.EffectRadius = Radius;

		consts.EffectFalloffRange = FalloffRange;
		consts.DenoiseBlurBeta = 1.2f; // Used only on Spatial denoising

		consts.NoiseIndex = DenoiseMode == DenoiseModes.Temporal ? Frame % 64 : 0;
		consts.ThinOccluderCompensation = ThinCompensation;
		consts.FinalValuePower = Intensity * 5.0f;

		switch ( Quality )
		{
			case SampleQuality.Low:
				consts.TAABlendAmount = 0.95f;
				break;
			case SampleQuality.Medium:
				consts.TAABlendAmount = 0.9f;
				break;
			case SampleQuality.High:
				consts.TAABlendAmount = 0.8f;
				break;
		}
		return consts;
	}

	//-------------------------------------------------------------------------

	public enum DenoiseModes
	{
		/// <summary>
		/// Applies spatial denoising to reduce noise by averaging pixel values within a local neighborhood.
		/// This method smooths out noise by considering the spatial relationship between pixels in a single frame.
		/// </summary>
		[Icon( "filter_center_focus" )]
		Spatial,

		/// <summary>
		/// Applies temporal denoising to reduce noise by averaging pixel values over multiple frames.
		/// This method leverages the temporal coherence of consecutive frames to achieve a noise-free result.
		/// </summary>
		[Icon( "auto_awesome_motion" )]
		Temporal
	}

	protected override void OnUpdate()
	{
		Rebuild();
	}

	void Rebuild()
	{
		if ( commands is null )
			return;

		commands.Reset();

		RenderTargetHandle ViewDepthChainTexture = commands.GetRenderTarget( "ViewDepthChainTexture", ImageFormat.R32F, numMips: 5 );
		RenderTargetHandle WorkingEdgesTexture = commands.GetRenderTarget( "WorkingEdgesTexture", ImageFormat.R16F );
		RenderTargetHandle WorkingAOTexture = commands.GetRenderTarget( "WorkingAOTexture", ImageFormat.A8 );
		RenderTargetHandle AOTexture0 = commands.GetRenderTarget( "AOTexture0", ImageFormat.A8 );
		RenderTargetHandle AOTexture1 = commands.GetRenderTarget( "AOTexture1", ImageFormat.A8 );

		bool pingPong = (Frame++ % 2) == 0;

		var AOTextureCurrent = pingPong ? AOTexture0 : AOTexture1;
		var AOTexturePrev = pingPong ? AOTexture1 : AOTexture0;

		var csAO = new ComputeShader( "gtao_cs" );

		commands.SetConstantBuffer( "GTAOConstants", GetGTAOConstants() );

		// 
		// Bind textures to the compute shader
		commands.Set( "WorkingDepthMIP0", ViewDepthChainTexture.ColorTexture, 0 );
		commands.Set( "WorkingDepthMIP1", ViewDepthChainTexture.ColorTexture, 1 );
		commands.Set( "WorkingDepthMIP2", ViewDepthChainTexture.ColorTexture, 2 );
		commands.Set( "WorkingDepthMIP3", ViewDepthChainTexture.ColorTexture, 3 );
		commands.Set( "WorkingDepthMIP4", ViewDepthChainTexture.ColorTexture, 4 );
		commands.Set( "WorkingDepth", ViewDepthChainTexture.ColorTexture );
		commands.Set( "WorkingAOTerm", WorkingAOTexture.ColorTexture );
		commands.Set( "WorkingEdges", WorkingEdgesTexture.ColorTexture );
		commands.Set( "FinalAOTerm", AOTextureCurrent.ColorTexture );
		commands.Set( "FinalAOTermPrev", AOTexturePrev.ColorTexture );

		commands.SetCombo( "D_QUALITY", Quality );

		// View depth chain
		{
			commands.SetCombo( "D_PASS", GTAOPasses.ViewDepthChain );
			commands.DispatchCompute( csAO, AOTextureCurrent.Size );
		}

		// Main pass
		{
			commands.SetCombo( "D_PASS", GTAOPasses.MainPass );
			commands.DispatchCompute( csAO, AOTextureCurrent.Size );
		}

		// Denoise
		{
			commands.SetCombo( "D_PASS", DenoiseMode == DenoiseModes.Temporal ? GTAOPasses.DenoiseTemporal : GTAOPasses.DenoiseSpatial );
			commands.DispatchCompute( csAO, AOTextureCurrent.Size );
		}

		//
		// Finally pass the AO as a texture for the rest of the pipeline
		// Technically uses previous frame texture since it'll be applied next frame
		// We could try to parent rather than merging attributes but it's causing race conditions from managed size and more complex to manage
		//
		commands.SetGlobal( "AmbientOcclusionIndex", AOTextureCurrent.ColorIndex );
	}
}
