using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Minimal RenderDoc in-application API bridge used only by automated research captures.</summary>
    internal static class RenderDocCaptureBridge
    {
        private const int RenderDocApiVersion_1_6_0 = 10600;
        private const int TriggerCaptureSlot = 15;

        [DllImport("renderdoc", CallingConvention = CallingConvention.Cdecl, EntryPoint = "RENDERDOC_GetAPI")]
        private static extern int GetApi(int version, out IntPtr api);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void TriggerCaptureDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void StartFrameDelegate(IntPtr device, IntPtr window);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint CaptureStateDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint EndFrameDelegate(IntPtr device, IntPtr window);

        // Explicit offscreen capture, only called by an opted-in synthetic diagnostic.
        // Slots follow the public RenderDoc 1.6 C ABI; never overlap an existing capture.
        public static bool BeginOffscreenCapture()
        {
            try
            {
                if (GetApi(RenderDocApiVersion_1_6_0, out var api) != 1 || api == IntPtr.Zero) return false;
                var state = Marshal.GetDelegateForFunctionPointer<CaptureStateDelegate>(Marshal.ReadIntPtr(api, 20 * IntPtr.Size));
                if (state() != 0) return false;
                Marshal.GetDelegateForFunctionPointer<StartFrameDelegate>(Marshal.ReadIntPtr(api, 19 * IntPtr.Size))(IntPtr.Zero, IntPtr.Zero);
                return state() != 0;
            }
            catch (Exception exception) { Debug.LogError("[RenderDoc] Offscreen capture unavailable: " + exception.GetType().Name); return false; }
        }

        public static bool EndOffscreenCapture()
        {
            try
            {
                if (GetApi(RenderDocApiVersion_1_6_0, out var api) != 1 || api == IntPtr.Zero) return false;
                return Marshal.GetDelegateForFunctionPointer<EndFrameDelegate>(Marshal.ReadIntPtr(api, 21 * IntPtr.Size))(IntPtr.Zero, IntPtr.Zero) == 1;
            }
            catch (Exception exception) { Debug.LogError("[RenderDoc] Offscreen capture end failed: " + exception.GetType().Name); return false; }
        }

        public static bool TriggerCapture()
        {
            try
            {
                IntPtr api;
                if (GetApi(RenderDocApiVersion_1_6_0, out api) != 1 || api == IntPtr.Zero)
                {
                    Debug.LogError("[RenderDoc] RENDERDOC_GetAPI failed");
                    return false;
                }

                IntPtr function = Marshal.ReadIntPtr(api, TriggerCaptureSlot * IntPtr.Size);
                if (function == IntPtr.Zero)
                {
                    Debug.LogError("[RenderDoc] TriggerCapture function is null");
                    return false;
                }

                Marshal.GetDelegateForFunctionPointer<TriggerCaptureDelegate>(function)();
                Debug.Log("[RenderDoc] Triggered next-frame capture through in-application API");
                return true;
            }
            catch (DllNotFoundException)
            {
                Debug.LogError("[RenderDoc] renderdoc.dll is not injected");
                return false;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return false;
            }
        }
    }
}
