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
