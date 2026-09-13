using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Optional Built-in camera adapter. Components/Animator supply current poses;
    /// this component never advances their clocks or mutates actor materials.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(Camera))]
    public sealed class FaceDecalLayer : MonoBehaviour
    {
        public bool decalsEnabled;
        public Texture atlas;
        public FaceDecalProjector[] projectors = Array.Empty<FaceDecalProjector>();
        public FaceDecalRenderer.Receiver[] receivers = Array.Empty<FaceDecalRenderer.Receiver>();
        public string UnavailableReason { get; private set; }
        public int SubmittedReceivers { get; private set; }
        private Camera cameraOwner;
        private CommandBuffer commands;
        private FaceDecalRenderer rendererOwner;
        private void OnEnable(){cameraOwner=GetComponent<Camera>();}
        private void OnPreCull()
        {
            SubmittedReceivers=0;UnavailableReason=null;
            commands?.Clear();
            if(!decalsEnabled){Release();return;}
            try
            {
                var target=cameraOwner.targetTexture;
                if(GraphicsSettings.currentRenderPipeline!=null||cameraOwner.stereoEnabled||cameraOwner.rect!=new Rect(0,0,1,1)||
                    (target!=null&&(target.dimension!=TextureDimension.Tex2D||target.antiAliasing>1||target.useDynamicScale||target.depth==0))||
                    (target==null&&cameraOwner.allowMSAA&&QualitySettings.antiAliasing>1))throw new NotSupportedException("Requires Built-in full viewport, depth, fixed-size non-XR/non-MSAA target.");
                if(projectors==null||projectors.Length>8)throw new ArgumentException("At most8 explicit projector components are supported.");
                var data=new System.Collections.Generic.List<FaceDecalProjector.Data>();
                foreach(var p in projectors){if(p==null)throw new ArgumentException("Missing projector component.");if(p.isActiveAndEnabled)data.Add(p.Snapshot());}
                if(receivers==null||receivers.Length>128)throw new ArgumentException("At most128 explicit receivers are supported.");
                var visible=new System.Collections.Generic.List<FaceDecalRenderer.Receiver>();
                foreach(var r in receivers)
                {
                    if(r==null||r.surface==null||r.surface.renderer==null)throw new ArgumentException("Missing receiver.");
                    if((cameraOwner.cullingMask&(1<<r.surface.renderer.gameObject.layer))!=0)visible.Add(r);
                }
                if(rendererOwner==null)rendererOwner=new FaceDecalRenderer();
                if(!rendererOwner.TryPrepare(atlas,data.ToArray(),visible.ToArray()))throw new InvalidOperationException(rendererOwner.UnavailableReason);
                if(rendererOwner.Draws.Count==0){Release();return;}
                if(commands==null){commands=new CommandBuffer{name="Toolkit authored face decals"};cameraOwner.AddCommandBuffer(CameraEvent.BeforeForwardAlpha,commands);}
                if(!rendererOwner.Record(commands))throw new InvalidOperationException(rendererOwner.UnavailableReason);
                SubmittedReceivers=rendererOwner.Draws.Count;
            }
            catch(Exception error){UnavailableReason=error.Message;Release();}
        }
        private void Release()
        {
            if(commands!=null){if(cameraOwner!=null)cameraOwner.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha,commands);commands.Clear();commands.Release();commands=null;}
            rendererOwner?.Dispose();rendererOwner=null;SubmittedReceivers=0;
        }
        private void OnDisable(){Release();}
        private void OnDestroy(){Release();}
    }
}
