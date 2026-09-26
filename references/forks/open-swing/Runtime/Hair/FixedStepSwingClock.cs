// Fixed-step pose input and display interpolation for an ActorAnimationSwingSolver.
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OpenSwing
{
public sealed class FixedStepSwingClock
{
    private const float Interval=1f/60f;
    private readonly ActorAnimationSwingSolver solver;
    private readonly Transform[] inputs,outputs;
    private Pose[] previousInput,currentInput;
    private readonly Pose[] previousOutput,currentOutput;
    private float remainder;
    private bool initialized;
    public int LastStepCount { get; private set; }
    public int TotalSteps { get; private set; }

    private struct Pose
    {
        public Vector3 position;
        public Quaternion rotation;
        public Pose(Transform t){position=t.localPosition;rotation=t.localRotation;}
        public void Apply(Transform t){t.localPosition=position;t.localRotation=rotation;}
        public static void Blend(Transform t,Pose a,Pose b,float fraction)
        {
            t.localPosition=Vector3.LerpUnclamped(a.position,b.position,fraction);
            t.localRotation=Quaternion.SlerpUnclamped(a.rotation,b.rotation,fraction);
        }
    }

    public FixedStepSwingClock(ActorAnimationSwingSolver value,Transform visualRoot)
    {
        solver=value;
        outputs=solver.SimulationBones();
        var relevant=new HashSet<Transform>();
        IEnumerable<Transform> bound = solver.bindings != null
            ? solver.bindings.Values : Enumerable.Empty<Transform>();
        foreach(var bone in bound.Concat(new[]{solver.transform,solver.rootMotionSource,solver.head,solver.neck}))
            for(var t=bone;t!=null;t=t.parent)
            {
                relevant.Add(t);
                if(t==visualRoot)break;
            }
        inputs=relevant.ToArray();
        previousInput=new Pose[inputs.Length];currentInput=new Pose[inputs.Length];
        previousOutput=new Pose[outputs.Length];currentOutput=new Pose[outputs.Length];
    }

    public void Reset(){initialized=false;remainder=0;}

    public void Advance(float dt)
    {
        LastStepCount=0;
        if(dt<=0)return;
        dt=Mathf.Min(dt,.1f);
        for(int i=0;i<inputs.Length;i++)currentInput[i]=new Pose(inputs[i]);
        if(!initialized || solver.ResetPending)
        {
            solver.CapturePose();solver.Step(ActorAnimationSwingSolver.NativeStep);
            LastStepCount=1;TotalSteps++;
            for(int i=0;i<outputs.Length;i++)previousOutput[i]=currentOutput[i]=new Pose(outputs[i]);
            remainder=0;initialized=true;
        }
        else
        {
            float nextTick=Interval-remainder;
            float elapsed=remainder+dt;
            while(elapsed+1e-7f>=Interval && LastStepCount<6)
            {
                float fraction=Mathf.Clamp01(nextTick/dt);
                for(int i=0;i<inputs.Length;i++)Pose.Blend(inputs[i],previousInput[i],currentInput[i],fraction);
                solver.CapturePose();solver.Step(ActorAnimationSwingSolver.NativeStep);
                for(int i=0;i<outputs.Length;i++)
                {
                    previousOutput[i]=currentOutput[i];
                    currentOutput[i]=new Pose(outputs[i]);
                }
                elapsed-=Interval;nextTick+=Interval;LastStepCount++;TotalSteps++;
            }
            remainder=Mathf.Max(0,elapsed);
        }
        for(int i=0;i<inputs.Length;i++)currentInput[i].Apply(inputs[i]);
        solver.CapturePose();
        float alpha=Mathf.Clamp01(remainder/Interval);
        for(int i=0;i<outputs.Length;i++)Pose.Blend(outputs[i],previousOutput[i],currentOutput[i],alpha);
        var swap=previousInput;previousInput=currentInput;currentInput=swap;
    }
}
}
