using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Selene
{
    // Preserves the GPU/VRAM suspension and additionally releases reclaimable image/cache memory when Playnite is minimized or hidden.
    // 1. Shuts down active video decoders (LibraryStage trailers, tile previews, backdrop videos) to release DXVA GPU engines and buffers.
    // 2. Unhooks render loops (CompositionTarget.Rendering, full refresh rate, glide clocks, smoothness monitors).
    // 3. Detaches active BitmapCache modes; WPF or the driver may retain their VRAM allocations while its device remains alive.
    // 4. Hides the root visual to stop presenting application content while keeping the tree available for quick restore.
    // 5. Releases decoded screenshot, launch-art and wallpaper-preview cache references before collecting unreachable images.
    // 6. Asks Windows to trim resident pages; committed/private bytes can remain allocated until reused or released by their owners.
    // 7. Restores caches, refresh rates, and visuals on window restore.
    public static class MinimizeOptimizer
    {
        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);

        private class CachedNode
        {
            public WeakReference Target;
            public CacheMode Mode;
        }

        private static Window watchedWindow;
        private static bool isMinimized;
        private static readonly List<CachedNode> savedCaches = new List<CachedNode>();
        private static Visibility savedRootVisibility = Visibility.Visible;
        private static UIElement savedRoot;
        private static object savedContent;
        private static BindingBase savedContentBinding;
        private static bool contentDetached;

        public static bool IsMinimized { get { return isMinimized; } }

        public static void Install(Window window)
        {
            if (window == null || ReferenceEquals(watchedWindow, window)) return;
            watchedWindow = window;

            window.StateChanged += OnWindowStateChanged;
            window.IsVisibleChanged += OnWindowIsVisibleChanged;

            CheckState(window);
            HomeNav.Log("gpu-opt: installed on MainWindow");
        }

        private static void OnWindowStateChanged(object sender, EventArgs e)
        {
            var w = sender as Window;
            if (w != null) CheckState(w);
        }

        private static void OnWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var w = sender as Window;
            if (w != null) CheckState(w);
        }

        public static void CheckState(Window window)
        {
            if (window == null) return;
            bool shouldMinimize = (window.WindowState == WindowState.Minimized || !window.IsVisible || CloseToTray.IsHidden);

            if (shouldMinimize && !isMinimized)
            {
                Suspend(window);
            }
            else if (!shouldMinimize && isMinimized)
            {
                Resume(window);
            }
        }

        public static void Suspend(Window window)
        {
            if (isMinimized) return;
            isMinimized = true;
            HomeNav.Log("gpu-opt: window minimized/hidden -> pausing renderers and releasing image caches");

            try
            {
                // 1. Stop all video playback immediately (DXVA hardware decoding shut down)
                LibraryStage.Stop();
                GameMedia.SuspendForMinimize();
                HomeNav.ReleasePreloadedArt();
                Wallpapers.ReleasePreloaded();
                HomeNav.PauseForOverlay();
                LoginScene.PauseVisuals(true);
                IdleScreen.Restart();

                // 2. Cancel active glide / smoothness render loops
                HomeNav.CancelGlide(null);
                Smoothness.Pause();
                Ui.FullRateChanged();

                // 3. Detach BitmapCache modes; WPF or the driver may retain the underlying allocations for reuse.
                savedCaches.Clear();
                CollectAndClearCaches(window);
                if (Application.Current != null)
                {
                    foreach (Window other in Application.Current.Windows)
                    {
                        if (other != null && !ReferenceEquals(other, window))
                        {
                            CollectAndClearCaches(other);
                        }
                    }
                }
                HomeNav.Log("gpu-opt: detached " + savedCaches.Count + " BitmapCache modes");

                // 4. Disconnect the root from WPF's presentation tree but retain its state for restoration.
                var root = window != null ? window.Content as UIElement : null;
                if (root != null)
                {
                    savedRoot = root;
                    savedRootVisibility = root.Visibility;
                    root.Visibility = Visibility.Hidden;
                }
                savedContent = window.Content;
                savedContentBinding = BindingOperations.GetBindingBase(window, ContentControl.ContentProperty);
                if (savedContentBinding != null) BindingOperations.ClearBinding(window, ContentControl.ContentProperty);
                window.Content = null;
                contentDetached = true;

                var trimTimer = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle, window.Dispatcher)
                { Interval = TimeSpan.FromMilliseconds(350) };
                trimTimer.Tick += (s, e) => { trimTimer.Stop(); TrimMemoryAfterDetach(); };
                trimTimer.Start();
                HomeNav.Log("gpu-opt: visual tree detached; post-decoder memory trim queued");
            }
            catch (Exception ex)
            {
                HomeNav.Log("gpu-opt: suspend error: " + ex.Message);
            }
        }

        public static void Resume(Window window)
        {
            if (!isMinimized) return;
            isMinimized = false;
            HomeNav.Log("gpu-opt: window restored -> reactivating visual tree");

            try
            {
                // 1. Restore root state and reconnect the content/binding.
                if (savedRoot != null)
                {
                    savedRoot.Visibility = savedRootVisibility == Visibility.Visible ? Visibility.Visible : savedRootVisibility;
                }
                if (contentDetached && window != null)
                {
                    if (savedContentBinding != null) BindingOperations.SetBinding(window, ContentControl.ContentProperty, savedContentBinding);
                    else window.Content = savedContent;
                    contentDetached = false;
                    savedContent = null;
                    savedContentBinding = null;
                }
                savedRoot = null;

                // 2. Restore BitmapCaches
                int restoredCount = 0;
                foreach (var node in savedCaches)
                {
                    if (node.Target != null && node.Target.IsAlive)
                    {
                        var el = node.Target.Target as UIElement;
                        if (el != null)
                        {
                            el.CacheMode = node.Mode;
                            restoredCount++;
                        }
                    }
                }
                savedCaches.Clear();
                HomeNav.Log("gpu-opt: restored " + restoredCount + " BitmapCache textures");

                // 3. Resume render loops and media
                Smoothness.Resume();
                Ui.FullRateChanged();
                LoginScene.PauseVisuals(false);
                GameMedia.ResumeAfterMinimize();

                HomeNav.Log("gpu-opt: resume complete");
            }
            catch (Exception ex)
            {
                HomeNav.Log("gpu-opt: resume error: " + ex.Message);
            }
        }

        private static void CollectAndClearCaches(DependencyObject root)
        {
            if (root == null) return;

            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();

                var el = current as UIElement;
                if (el != null && el.CacheMode != null)
                {
                    savedCaches.Add(new CachedNode { Target = new WeakReference(el), Mode = el.CacheMode.CloneCurrentValue() });
                    el.CacheMode = null;
                }

                int childrenCount = VisualTreeHelper.GetChildrenCount(current);
                for (int i = 0; i < childrenCount; i++)
                {
                    var child = VisualTreeHelper.GetChild(current, i);
                    if (child != null) queue.Enqueue(child);
                }
            }
        }

        private static void TrimMemoryAfterDetach()
        {
            if (!isMinimized) return;
            try
            {
                GC.Collect(2, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true);
                EmptyWorkingSet(Process.GetCurrentProcess().Handle);
                HomeNav.Log("gpu-opt: detached visual tree; reclaimable memory trim complete");
            }
            catch (Exception ex) { HomeNav.Log("gpu-opt: memory trim failed: " + ex.Message); }
        }
    }
}
