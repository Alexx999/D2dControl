﻿using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DeviceContext = SharpDX.Direct2D1.DeviceContext;

namespace D2dControl {
    public abstract class D2dControl : System.Windows.Controls.Image {
        // - field -----------------------------------------------------------------------

        private SharpDX.Direct3D11.Device device         ;
        private Texture2D                 sharedTarget   ;
        private Texture2D                 dx11Target     ;
        private Dx11ImageSource           d3DSurface     ;
        private SharpDX.Direct2D1.DeviceContext              d2DRenderTarget;

        private readonly Stopwatch renderTimer = new Stopwatch();

        protected ResourceCache resCache = new ResourceCache();

        private long lastFrameTime       = 0;
        private int  frameCount          = 0;
        private int  frameCountHistTotal = 0;
        private Queue<int> frameCountHist = new Queue<int>();

        // - property --------------------------------------------------------------------

        public static bool IsInDesignMode {
            get {
                var prop = DesignerProperties.IsInDesignModeProperty;
                var isDesignMode = (bool)DependencyPropertyDescriptor.FromProperty(prop, typeof(FrameworkElement)).Metadata.DefaultValue;
                return isDesignMode;
            }
        }

        private static readonly DependencyPropertyKey FpsPropertyKey = DependencyProperty.RegisterReadOnly(
            "Fps",
            typeof(int),
            typeof(D2dControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.None)
            );

        public static readonly DependencyProperty FpsProperty = FpsPropertyKey.DependencyProperty;

        public int Fps {
            get { return (int)GetValue( FpsProperty ); }
            protected set { SetValue( FpsPropertyKey, value ); }
        }

        public static readonly DependencyPropertyKey FrameTimePropertyKey = DependencyProperty.RegisterReadOnly("FrameTime", typeof(double), typeof(D2dControl), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.None));
        public double FrameTime {
            get { return (double)GetValue( FrameTimePropertyKey.DependencyProperty ); }
            protected set { SetValue( FrameTimePropertyKey, value ); }
        }

        public DeviceContext D2DRenderTarget => d2DRenderTarget;

        // - public methods --------------------------------------------------------------

        public D2dControl() {
            base.Loaded   += Window_Loaded;
            base.Unloaded += Window_Closing;

            base.Stretch = System.Windows.Media.Stretch.Fill;
        }

        public abstract void Render( SharpDX.Direct2D1.DeviceContext target );

        // - event handler ---------------------------------------------------------------

        private void Window_Loaded( object sender, RoutedEventArgs e ) {
            if ( D2dControl.IsInDesignMode ) {
                return;
            }

            StartD3D();
            StartRendering();
        }

        private void Window_Closing( object sender, RoutedEventArgs e ) {
            if ( D2dControl.IsInDesignMode ) {
                return;
            }
            Shutdown();
        }

        protected void Shutdown()
        {
            StopRendering();
            EndD3D();
        }

        private void OnRendering( object sender, EventArgs e ) {
            if ( !renderTimer.IsRunning ) {
                return;
            }
            
            frameTimer.Restart();
            PrepareAndCallRender();
            d3DSurface.Lock();
            device.ImmediateContext.ResolveSubresource(dx11Target, 0, sharedTarget, 0, Format.B8G8R8A8_UNorm);
            d3DSurface.InvalidateD3DImage();
            d3DSurface.Unlock();
            FrameTime =_timeHelper.Push(frameTimer.Elapsed.TotalMilliseconds);
        }

        protected override void OnRenderSizeChanged( SizeChangedInfo sizeInfo ) {
            CreateAndBindTargets();
            base.OnRenderSizeChanged( sizeInfo );
        }

        private void OnIsFrontBufferAvailableChanged( object sender, DependencyPropertyChangedEventArgs e ) {
            if ( d3DSurface.IsFrontBufferAvailable ) {
                StartRendering();
            } else {
                StopRendering();
            }
        }

        // - private methods -------------------------------------------------------------

        private void StartD3D() {
            device = new SharpDX.Direct3D11.Device( DriverType.Hardware, DeviceCreationFlags.BgraSupport |
#if DEBUG
                                                                         DeviceCreationFlags.Debug |
#endif
                                                                         DeviceCreationFlags.None);

            d3DSurface = new Dx11ImageSource();
            d3DSurface.IsFrontBufferAvailableChanged += OnIsFrontBufferAvailableChanged;

            CreateAndBindTargets();

            base.Source = d3DSurface;
        }

        private void EndD3D() {
            d3DSurface.IsFrontBufferAvailableChanged -= OnIsFrontBufferAvailableChanged;
            base.Source = null;

            Disposer.SafeDispose( ref d2DRenderTarget );
            Disposer.SafeDispose( ref d3DSurface );
            Disposer.SafeDispose( ref sharedTarget );
            Disposer.SafeDispose( ref dx11Target );
            Disposer.SafeDispose( ref device );
        }

        private void CreateAndBindTargets() {
            d3DSurface.SetRenderTarget( null );

            Disposer.SafeDispose( ref d2DRenderTarget );
            Disposer.SafeDispose( ref sharedTarget );
            Disposer.SafeDispose( ref dx11Target );

            var width  = Math.Max((int)ActualWidth , 100);
            var height = Math.Max((int)ActualHeight, 100);

            var frontDesc = new Texture2DDescription {
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                Format = Format.B8G8R8A8_UNorm,
                Width = width,
                Height = height,
                MipLevels = 1,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                OptionFlags = ResourceOptionFlags.Shared,
                CpuAccessFlags = CpuAccessFlags.None,
                ArraySize = 1
            };

            var backDesc = new Texture2DDescription {
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                Format = Format.B8G8R8A8_UNorm,
                Width = width,
                Height = height,
                MipLevels = 1,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                OptionFlags = ResourceOptionFlags.None,
                CpuAccessFlags = CpuAccessFlags.None,
                ArraySize = 1
            };

            sharedTarget = new Texture2D( device, frontDesc );
            dx11Target = new Texture2D( device, backDesc );

            using (var surface = dx11Target.QueryInterface<Surface>())
            {

                d2DRenderTarget = new SharpDX.Direct2D1.DeviceContext(surface, new CreationProperties()
                {
                    Options = DeviceContextOptions.EnableMultithreadedOptimizations,
#if DEBUG
                    DebugLevel = DebugLevel.Information,
#endif
                    ThreadingMode = ThreadingMode.SingleThreaded
                });
            }

            resCache.RenderTarget = d2DRenderTarget;

            d3DSurface.SetRenderTarget( sharedTarget );

            device.ImmediateContext.Rasterizer.SetViewport( 0, 0, width, height, 0.0f, 1.0f );
            TargetsCreated();
        }

        protected virtual void TargetsCreated()
        {
        }

        private void StartRendering() {
            if ( renderTimer.IsRunning ) {
                return;
            }

            System.Windows.Media.CompositionTarget.Rendering += OnRendering;
            renderTimer.Start();
        }

        private void StopRendering() {
            if ( !renderTimer.IsRunning ) {
                return;
            }

            System.Windows.Media.CompositionTarget.Rendering -= OnRendering;
            renderTimer.Stop();
        }

        private Stopwatch frameTimer = new Stopwatch();
        private FrameTimeHelper _timeHelper = new FrameTimeHelper(60);
        private void PrepareAndCallRender() {
            if ( device == null ) {
                return;
            }
            
            d2DRenderTarget.BeginDraw();
            Render( d2DRenderTarget );
            d2DRenderTarget.EndDraw();

            CalcFps();

            device.ImmediateContext.Flush();
        }

        private void CalcFps() {
            frameCount++;
            if ( renderTimer.ElapsedMilliseconds - lastFrameTime > 1000 ) {
                frameCountHist.Enqueue( frameCount );
                frameCountHistTotal += frameCount;
                if ( frameCountHist.Count > 5 ) {
                    frameCountHistTotal -= frameCountHist.Dequeue();
                }

                Fps = frameCountHistTotal / frameCountHist.Count;

                frameCount = 0;
                lastFrameTime = renderTimer.ElapsedMilliseconds;
            }
        }
    }
}
