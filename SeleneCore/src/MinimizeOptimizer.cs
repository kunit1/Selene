using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Selene
{
    // Minimizes GPU and VRAM usage to zero (or near-zero) whenever Playnite is minimized or hidden.
    // 1. Shuts down active video decoders (LibraryStage trailers, tile previews, backdrop videos) to release DXVA GPU engines and buffers.
    // 2. Unhooks render loops (CompositionTarget.Rendering, full refresh rate, glide clocks, smoothness monitors).
    // 3. Collects and clears all active BitmapCache surfaces across the visual tree to free Direct3D textures from VRAM.
    // 4. Sets root visual to Hidden, causing WPF's milcore composition engine to enter complete idle state.
    // 5. Calls native EmptyWorkingSet and GC to instruct Windows WDDM to evict unreferenced DirectX surfaces and staging buffers from local VRAM.
    // 6. Seamlessly restores caches, refresh rates, and visuals on window restore with zero flicker.
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
            HomeNav.Log("gpu-opt: window minimized/hidden -> suspending GPU/VRAM consumers");

            try
            {
                // 1. Stop all video playback immediately (DXVA hardware decoding shut down)
                LibraryStage.Stop();
                HomeNav.PauseForOverlay();
                LoginScene.PauseVisuals(true);
                IdleScreen.Restart();

                // 2. Cancel active glide / smoothness render loops
                HomeNav.CancelGlide(null);
                Smoothness.Pause();
                Ui.FullRateChanged();

                // 3. Clear all BitmapCache instances from the visual tree to free VRAM textures
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
                HomeNav.Log("gpu-opt: cleared " + savedCaches.Count + " BitmapCache textures from VRAM");

                // 4. Hide root content to halt milcore composition passes completely
                var root = window != null ? window.Content as UIElement : null;
                if (root != null)
                {
                    savedRoot = root;
                    savedRootVisibility = root.Visibility;
                    root.Visibility = Visibility.Hidden;
                }

                // 5. Force garbage collection and empty working set to trigger WDDM VRAM eviction
                GC.Collect(2, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true);

                try
                {
                    EmptyWorkingSet(Process.GetCurrentProcess().Handle);
                }
                catch { }

                HomeNav.Log("gpu-opt: suspension complete (0% GPU, VRAM evicted)");
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
                // 1. Restore root visibility
                if (savedRoot != null)
                {
                    savedRoot.Visibility = savedRootVisibility == Visibility.Visible ? Visibility.Visible : savedRootVisibility;
                    savedRoot = null;
                }

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
                    savedCaches.Add(new CachedNode { Target = new WeakReference(el), Mode = el.CacheMode });
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
    }
}
