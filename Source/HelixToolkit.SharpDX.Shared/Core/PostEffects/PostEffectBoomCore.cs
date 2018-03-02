﻿/*
The MIT License (MIT)
Copyright (c) 2018 Helix Toolkit contributors
*/
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using System.Collections.Generic;
using System.Linq;

#if !NETFX_CORE
namespace HelixToolkit.Wpf.SharpDX.Core
#else
namespace HelixToolkit.UWP.Core
#endif
{
    using Utilities;
    using Render;
    using Shaders;
    using Model;
    using System;

    public interface IPostEffectBloom : IPostEffect
    {
        Color4 ThresholdColor { set; get; }
        float BloomExtractIntensity { set; get; }
        float BloomPassIntensity { set; get; }

        float BloomCombineSaturation { set; get; }

        float BloomCombineIntensity { set; get; }
    }
    /// <summary>
    /// Outline blur effect
    /// <para>Must not put in shared model across multiple viewport, otherwise may causes performance issue if each viewport sizes are different.</para>
    /// </summary>
    public class PostEffectBloomCore : RenderCoreBase<BorderEffectStruct>, IPostEffectBloom
    {
        /// <summary>
        /// Gets or sets the name of the effect.
        /// </summary>
        /// <value>
        /// The name of the effect.
        /// </value>
        public string EffectName
        {
            set; get;
        } = DefaultRenderTechniqueNames.PostEffectBloom;

        /// <summary>
        /// Gets or sets the color of the border.
        /// </summary>
        /// <value>
        /// The color of the border.
        /// </value>
        public Color4 ThresholdColor
        {
            set
            {
                SetAffectsRender(ref modelStruct.Color, value);
            }
            get { return modelStruct.Color; }
        }

        public float BloomExtractIntensity
        {
            set
            {
                SetAffectsRender(ref modelStruct.Param.M11, value);
            }
            get { return modelStruct.Param.M11; }
        }

        public float BloomPassIntensity
        {
            set
            {
                SetAffectsRender(ref modelStruct.Param.M12, value);
            }
            get { return modelStruct.Param.M12; }
        }

        public float BloomCombineSaturation
        {
            set
            {
                SetAffectsRender(ref modelStruct.Param.M13, value);
            }
            get { return modelStruct.Param.M13; }
        }

        public float BloomCombineIntensity
        {
            set
            {
                SetAffectsRender(ref modelStruct.Param.M14, value);
            }
            get { return modelStruct.Param.M14; }
        }

        private int numberOfBlurPass = 2;
        /// <summary>
        /// Gets or sets the number of blur pass.
        /// </summary>
        /// <value>
        /// The number of blur pass.
        /// </value>
        public int NumberOfBlurPass
        {
            set
            {
                SetAffectsRender(ref numberOfBlurPass, value);
            }
            get { return numberOfBlurPass; }
        }

        private int maximumDownSamplingStep = 3;
        /// <summary>
        /// Gets or sets the maximum down sampling step.
        /// </summary>
        /// <value>
        /// The maximum down sampling step.
        /// </value>
        public int MaximumDownSamplingStep
        {
            set
            {
                SetAffectsRender(ref maximumDownSamplingStep, value);
            }
            get { return maximumDownSamplingStep; }
        }

        private IShaderPass screenQuadPass;

        private IShaderPass screenQuadCopy;

        private IShaderPass blurPassVertical;

        private IShaderPass blurPassHorizontal;

        private IShaderPass screenOutlinePass;
        #region Texture Resources

        private readonly IList<PostEffectBlurCore> renderTargetFull = new List<PostEffectBlurCore>();

        private int textureSlot;

        private int samplerSlot;

        private SamplerState sampler;

        private int width, height;
        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="PostEffectMeshOutlineBlurCore"/> class.
        /// </summary>
        public PostEffectBloomCore() : base(RenderType.PostProc)
        {
            ThresholdColor = new Color4(0.8f, 0.8f, 0.8f, 0f);
            BloomExtractIntensity = 1f;
            BloomPassIntensity = 0.95f;
            BloomCombineIntensity = 0.7f;
            BloomCombineSaturation = 0.7f;
        }

        protected override ConstantBufferDescription GetModelConstantBufferDescription()
        {
            return new ConstantBufferDescription(DefaultBufferNames.BorderEffectCB, BorderEffectStruct.SizeInBytes);
        }

        protected override bool OnAttach(IRenderTechnique technique)
        {
            if (base.OnAttach(technique))
            {
                screenQuadPass = technique.GetPass(DefaultPassNames.ScreenQuad);
                screenQuadCopy = technique.GetPass(DefaultPassNames.ScreenQuadCopy);
                blurPassVertical = technique.GetPass(DefaultPassNames.EffectBlurVertical);
                blurPassHorizontal = technique.GetPass(DefaultPassNames.EffectBlurHorizontal);
                screenOutlinePass = technique.GetPass(DefaultPassNames.MeshOutline);
                textureSlot = screenOutlinePass.GetShader(ShaderStage.Pixel).ShaderResourceViewMapping.TryGetBindSlot(DefaultBufferNames.DiffuseMapTB);
                samplerSlot = screenOutlinePass.GetShader(ShaderStage.Pixel).SamplerMapping.TryGetBindSlot(DefaultSamplerStateNames.DiffuseMapSampler);
                sampler = Collect(technique.EffectsManager.StateManager.Register(DefaultSamplers.LinearSamplerClampAni4));
                return true;
            }
            else
            {
                return false;
            }
        }

        protected override bool CanRender(IRenderContext context)
        {
            return IsAttached && !string.IsNullOrEmpty(EffectName);
        }

        protected override void OnRender(IRenderContext context, DeviceContextProxy deviceContext)
        {
            DepthStencilView dsView;
            var renderTargets = deviceContext.DeviceContext.OutputMerger.GetRenderTargets(1, out dsView);
            if (dsView == null)
            {
                return;
            }
            #region Initialize textures
            if (renderTargetFull.Count == 0
                || width != (int)(context.ActualWidth)
                || height != (int)(context.ActualHeight))
            {
                width = (int)(context.ActualWidth);
                height = (int)(context.ActualHeight);
                for (int i = 0; i < renderTargetFull.Count; ++i)
                {
                    var target = renderTargetFull[i];
                    RemoveAndDispose(ref target);
                }
                renderTargetFull.Clear();

                int w = width;
                int h = height;
                int count = 0;
                while(w > 1 && h > 1 && count < Math.Max(0, MaximumDownSamplingStep) + 1)
                {
                    var target = Collect(new PostEffectBlurCore(global::SharpDX.DXGI.Format.B8G8R8A8_UNorm, blurPassVertical, blurPassHorizontal, textureSlot, samplerSlot,
                        DefaultSamplers.LinearSamplerClampAni4, EffectTechnique.EffectsManager));
                    target.Resize(Device, w, h);
                    renderTargetFull.Add(target);
                    w >>= 2;
                    h >>= 2;
                    ++count;
                }
            }
            #endregion

            #region Render objects onto offscreen texture    
            using (var resource1 = renderTargets[0].Resource)
            {
                using (var resource2 = renderTargetFull[0].CurrentRTV.Resource)
                {
                    deviceContext.DeviceContext.ResolveSubresource(resource1, 0, resource2, 0, global::SharpDX.DXGI.Format.B8G8R8A8_UNorm);
                }
            }               
            #endregion

            deviceContext.DeviceContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
            #region Do Bloom Pass

            //Extract bloom samples
            BindTarget(null, renderTargetFull[0].NextRTV, deviceContext, renderTargetFull[0].Width, renderTargetFull[0].Height, false);
            screenQuadPass.GetShader(ShaderStage.Pixel).BindSampler(deviceContext, samplerSlot, sampler);
            screenQuadPass.GetShader(ShaderStage.Pixel).BindTexture(deviceContext, textureSlot, renderTargetFull[0].CurrentSRV);
            screenQuadPass.BindShader(deviceContext);
            screenQuadPass.BindStates(deviceContext, StateType.BlendState | StateType.RasterState | StateType.DepthStencilState);
            deviceContext.DeviceContext.Draw(4, 0);
            renderTargetFull[0].SwapTargets();

            // Down sampling
            screenQuadCopy.GetShader(ShaderStage.Pixel).BindSampler(deviceContext, samplerSlot, sampler);
            screenQuadCopy.BindShader(deviceContext);
            screenQuadCopy.BindStates(deviceContext, StateType.BlendState | StateType.RasterState | StateType.DepthStencilState);
            for (int i = 1; i < renderTargetFull.Count; ++i)
            {
                BindTarget(null, renderTargetFull[i].CurrentRTV, deviceContext, renderTargetFull[i].Width, renderTargetFull[i].Height, false);
                screenQuadCopy.GetShader(ShaderStage.Pixel).BindTexture(deviceContext, textureSlot, renderTargetFull[i - 1].CurrentSRV);
                deviceContext.DeviceContext.Draw(4, 0);
            }

            for (int i = renderTargetFull.Count - 1; i >= 1; --i)
            {
                //Run blur pass
                renderTargetFull[i].Run(deviceContext, NumberOfBlurPass);

                //Up sampling
                screenOutlinePass.GetShader(ShaderStage.Pixel).BindSampler(deviceContext, samplerSlot, sampler);
                screenOutlinePass.BindShader(deviceContext);
                screenOutlinePass.BindStates(deviceContext, StateType.BlendState | StateType.RasterState | StateType.DepthStencilState);
                BindTarget(null, renderTargetFull[i - 1].CurrentRTV, deviceContext, renderTargetFull[i - 1].Width, renderTargetFull[i - 1].Height, false);
                screenOutlinePass.GetShader(ShaderStage.Pixel).BindTexture(deviceContext, textureSlot, renderTargetFull[i].CurrentSRV);
                deviceContext.DeviceContext.Draw(4, 0);
            }
            renderTargetFull[0].Run(deviceContext, NumberOfBlurPass);
            #endregion

            #region Draw outline onto original target
            context.RenderHost.SetDefaultRenderTargets(false);
            screenOutlinePass.GetShader(ShaderStage.Pixel).BindSampler(deviceContext, samplerSlot, sampler);
            screenOutlinePass.GetShader(ShaderStage.Pixel).BindTexture(deviceContext, textureSlot, renderTargetFull[0].CurrentSRV);
            screenOutlinePass.BindShader(deviceContext);
            screenOutlinePass.BindStates(deviceContext, StateType.BlendState | StateType.RasterState | StateType.DepthStencilState);
            deviceContext.DeviceContext.Draw(4, 0);
            screenOutlinePass.GetShader(ShaderStage.Pixel).BindTexture(deviceContext, textureSlot, null);
            #endregion

            //Decrement ref count. See OutputMerger.GetRenderTargets remarks
            dsView.Dispose();
            foreach (var t in renderTargets)
            { t.Dispose(); }
        }

        protected override void OnDetach()
        {
            width = height = 0;
            renderTargetFull.Clear();
            base.OnDetach();
        }

        private static void BindTarget(DepthStencilView dsv, RenderTargetView targetView, DeviceContext context, int width, int height, bool clear = true)
        {
            if (clear)
            {
                context.ClearRenderTargetView(targetView, global::SharpDX.Color.Transparent);
            }
            context.OutputMerger.SetRenderTargets(dsv, new RenderTargetView[] { targetView });
            context.Rasterizer.SetViewport(0, 0, width, height);
            context.Rasterizer.SetScissorRectangle(0, 0, width, height);
        }

        protected override void OnUpdatePerModelStruct(ref BorderEffectStruct model, IRenderContext context)
        {
        }
    }
}
