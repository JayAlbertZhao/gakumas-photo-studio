using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Campus.Common;
using UnityEngine;
using VL.FaceSystem;

namespace GakumasPhotoMode
{
    [DefaultExecutionOrder(950)]
    public sealed partial class FaceExpressionRenderer : MonoBehaviour
    {
        private VLActorFaceModel _shapeSource;
        private VLActorFaceModel _weightDriver;
        private MeshRenderer _renderer;
        private MeshFilter _filter;
        private Mesh _deformedMesh;
        private Vector3[] _baseVertices;
        private Vector3[] _baseNormals;
        private Vector4[] _baseTangents;
        private Vector3[] _blendVertices;
        private Vector3[] _workingVertices;
        private Vector3[] _workingNormals;
        private Vector4[] _workingTangents;
        private Matrix4x4[] _skinMatrices;
        private bool[] _boneMotion;
        private float[] _lastWeights;
        private bool _boneSkinningReady;
        private bool _boneSkinningDisabled;
        private bool _legacyRelativeHeadSkinning;
        private float _maximumBoneDisplacement;
        private int _presetIndex;
        private int _debugShape = -1;
        private float _debugWeight = 1f;
        private bool _ready;
        private Transform _leftEye;
        private Transform _rightEye;
        private Camera _lookCamera;
        private Quaternion _leftEyeRest;
        private Quaternion _rightEyeRest;
        private const float OriginalBlinkDuration = 0.23333333432674408f;
        private const string OriginalBlinkShapeName = "b_eye.eye_001";
        private static readonly string[] OriginalEyeLimitShapeNames =
        {
            "b_eye.eye_001", "b_eye.eye_002", "b_eye.eye_003", "b_eye.eye_004",
            "b_eye.eye_005", "b_eye.eye_006", "b_eye.eye_007", "b_eye.eye_008",
            "b_eye.eye_009", "b_eye.eye_010", "b_eye.eye_011", "b_eye.eye_012",
            "b_eye.eye_013", "b_eye.eye_014", "b_eye.eye_201", "b_eye.eye_202",
            "b_eye.eye_203", "b_eye.eye_204",
        };
        private static readonly float[] OriginalEyeLimitThresholds =
        {
            0.80f, 0.80f, 0.80f, 0.10f, 0.10f, 1.00f, 1.00f, 1.00f,
            1.00f, 1.00f, 0.10f, 0.10f, 0.80f, 0.10f, 1.00f, 1.00f,
            1.00f, 1.00f,
        };
        private static readonly AnimationCurve OriginalBlinkCurve = BuildOriginalBlinkCurve();
        private int _blinkShapeIndex = -1;
        private int[] _eyeLimitShapeIndices = new int[0];
        private float _nextBlinkTime;
        private float _blinkElapsed = OriginalBlinkDuration;
        private bool _blinkActive;
        private bool _blinkWritePending;
        private bool _automaticBlinkEnabled = true;
        private bool _autoBlinkAllowed;
        private bool _eyeLimitActive;
        private bool _traceBlink;
        private float _blinkWeight;
        private float _gazeYaw;
        private float _gazePitch;
        private bool _storyGazeActive;
        private bool _preserveAnimatedGaze;
        private bool _capturedLookAtOwned;
        private float _storyGazeYaw;
        private float _storyGazePitch;
        private bool _storyBlinkActive;
        private float _storyBlinkProgress;
        private float _storyBlinkWeight;
        private CampusActorFaceCorrection _faceCorrection;
        private CampusActorEyeHighlight _eyeHighlight;
        private MaterialPropertyBlock _eyeHighlightPropertyBlock;
        private int[] _eyeHighlightMaterialIndices = new int[0];
        private Vector4 _eyeHighlightBaseMapTransform = new Vector4(1f, 1f, 0f, 0f);
        private readonly float[] _faceCorrectionWeights = new float[4];
        private float _faceCorrectionYaw;
        private float _faceCorrectionPitch;
        private Transform _faceCorrectionPoseTarget;
        private Vector3 _faceCorrectionSourceLocalEuler;
        private Vector3 _faceCorrectionSourceWorldEuler;
        private int _viewProfileSlot = -1;

        public bool ViewProfileCorrectionEnabled { get; set; } = true;
        public bool ViewProfileCorrectionAvailable { get { return _viewProfileSlot >= 0; } }
        public float ViewProfileAngle { get; private set; }
        public float ViewProfileWeight { get; private set; }

        public string[] PresetNames { get { return Presets.Select(value => value.name).ToArray(); } }
        public int PresetIndex { get { return _presetIndex; } }
        public string CurrentPresetName { get { return Presets[_presetIndex].name; } }
        public int ShapeCount { get { return _shapeSource == null || _shapeSource.blendShapes == null ? 0 : _shapeSource.blendShapes.Count; } }
        public int ActiveWeightCount { get; private set; }
        public float MaximumWeight { get; private set; }
        public float BlinkWeight { get { return Mathf.Max(_blinkWeight, _storyBlinkWeight); } }
        public Vector2 GazeAngles { get { return new Vector2(_gazeYaw, _gazePitch); } }
        public float EyeHighlightOffset { get; private set; }
        public Vector2 FaceCorrectionAngles { get { return new Vector2(_faceCorrectionYaw, _faceCorrectionPitch); } }
        public bool BoneSkinningReady { get { return _boneSkinningReady; } }
        public float MaximumBoneDisplacement { get { return IsGpuDeformationActive ? float.NaN : _maximumBoneDisplacement; } }
        public bool StoryGazeActive { get { return _storyGazeActive; } }
        public bool HasEyeBones { get { return _leftEye != null && _rightEye != null; } }
        public Vector3 EyeCenterPosition
        {
            get
            {
                return HasEyeBones
                    ? (_leftEye.position + _rightEye.position) * 0.5f
                    : transform.position;
            }
        }

        public void SetFaceCorrectionPoseTarget(Transform target)
        {
            _faceCorrectionPoseTarget = target;
        }

        private static readonly ExpressionPreset[] Presets =
        {
            new ExpressionPreset("FOLLOW MOTION"),
            new ExpressionPreset("NEUTRAL"),
            // Shape assignments are selected from the original fktn face set and
            // visually validated by the editor contact-sheet build.
            new ExpressionPreset("SOFT SMILE", new WeightedShape(2, 0.22f), new WeightedShape(42, 0.72f)),
            new ExpressionPreset("BRIGHT SMILE", new WeightedShape(5, 0.32f), new WeightedShape(21, 0.42f), new WeightedShape(55, 0.88f)),
            new ExpressionPreset("SERIOUS", new WeightedShape(8, 0.62f), new WeightedShape(27, 0.16f), new WeightedShape(45, 0.28f)),
            new ExpressionPreset("SURPRISED", new WeightedShape(12, 0.46f), new WeightedShape(47, 0.82f)),
            new ExpressionPreset("WINK", new WeightedShape(3, 0.18f), new WeightedShape(23, 0.92f), new WeightedShape(42, 0.68f)),
        };

        public bool Initialize(VLActorFaceModel shapeSource, VLActorFaceModel weightDriver)
        {
            _shapeSource = shapeSource;
            _weightDriver = weightDriver;
            _faceCorrection = GetComponent<CampusActorFaceCorrection>();
            _eyeHighlight = GetComponent<CampusActorEyeHighlight>();
            _renderer = GetComponentInChildren<MeshRenderer>(true);
            _filter = _renderer == null ? null : _renderer.GetComponent<MeshFilter>();
            Mesh sourceMesh = _filter == null ? null : _filter.sharedMesh;
            if (_shapeSource == null || _weightDriver == null || _filter == null || sourceMesh == null ||
                _shapeSource.blendShapes == null || _shapeSource.blendShapes.Count == 0)
            {
                Debug.LogWarning(string.Format("[PhotoMode] Face deformation unavailable: model={0}, driver={1}, filter={2}, mesh={3}, shapes={4}",
                    _shapeSource != null, _weightDriver != null, _filter != null, sourceMesh != null, ShapeCount));
                return false;
            }

            try
            {
                _deformedMesh = Instantiate(sourceMesh);
                _deformedMesh.name = sourceMesh.name + "__expressive";
                _filter.sharedMesh = _deformedMesh;
                _baseVertices = _deformedMesh.vertices;
                _baseNormals = _deformedMesh.normals;
                _baseTangents = _deformedMesh.tangents;
                _blendVertices = new Vector3[_baseVertices.Length];
                _workingVertices = new Vector3[_baseVertices.Length];
                _workingNormals = _baseNormals != null && _baseNormals.Length == _baseVertices.Length
                    ? new Vector3[_baseVertices.Length] : null;
                _workingTangents = _baseTangents != null && _baseTangents.Length == _baseVertices.Length
                    ? new Vector4[_baseVertices.Length] : null;
                Array.Copy(_baseVertices, _blendVertices, _baseVertices.Length);
                _boneSkinningDisabled = Environment.GetCommandLineArgs().Contains("--legacy-static-face-bones");
                _legacyRelativeHeadSkinning = Environment.GetCommandLineArgs()
                    .Contains("--legacy-relative-head-face-skinning");
                _boneSkinningReady = !_boneSkinningDisabled &&
                    _shapeSource.bones != null && _shapeSource.bindposes != null &&
                    _shapeSource.boneWeightAndIndices != null && _shapeSource.bones.Length > 0 &&
                    _shapeSource.bones.Length == _shapeSource.bindposes.Length &&
                    _shapeSource.boneWeightAndIndices.Length == _baseVertices.Length * 4 &&
                    _shapeSource.bones[0] != null;
                _skinMatrices = _boneSkinningReady
                    ? new Matrix4x4[_shapeSource.bones.Length] : new Matrix4x4[0];
                _boneMotion = _boneSkinningReady
                    ? new bool[_shapeSource.bones.Length] : new bool[0];
                _lastWeights = new float[_shapeSource.blendShapes.Count];
                for (int index = 0; index < _lastWeights.Length; index++) _lastWeights[index] = float.NaN;
                _ready = true;
                ResolveViewProfileCorrection();
                ResolveEyeHighlightMaterialContract();
                ResolveOriginalBlinkContract();
                _nextBlinkTime = UnityEngine.Random.Range(3f, 6f);
                _blinkElapsed = OriginalBlinkDuration;
                _blinkActive = false;
                _blinkWeight = 0f;
                _traceBlink = Environment.GetCommandLineArgs().Contains("--trace-original-blink");
                _gpuDeformationEnabled |= Environment.GetCommandLineArgs().Contains("--gpu-face-deformation");
                Debug.Log(string.Format(
                    "[PhotoMode] Face deformation ready: {0} vertices, {1} shapes, correction={2}, eyeHighlight={3}, customSkinning={4}, bones={5}",
                    _baseVertices.Length, ShapeCount, _faceCorrection != null, _eyeHighlight != null,
                    _boneSkinningReady, _shapeSource.bones == null ? 0 : _shapeSource.bones.Length));
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return false;
            }
        }

        public void SelectPreset(int index)
        {
            _presetIndex = (index % Presets.Length + Presets.Length) % Presets.Length;
            _debugShape = -1;
            ForceRefresh();
        }

        public void SetDebugShape(int index, float weight)
        {
            _debugShape = index;
            _debugWeight = weight;
            ForceRefresh();
        }

        public void ClearDebugShape()
        {
            _debugShape = -1;
            ForceRefresh();
        }

        public void ForceBlink()
        {
            _blinkActive = true;
            _blinkElapsed = 0f;
            _blinkWeight = 0f;
            _nextBlinkTime = UnityEngine.Random.Range(3f, 6f);
        }

        public void SetAutomaticBlinkEnabled(bool enabled)
        {
            // PhotographyActor.StartFacialMotionAsync forwards
            // !PhotoFacialMotionGroup.disableAutoBlink to
            // Actor.SetAutoEyeBlinkActive. Disabling does not cancel a blink
            // already in progress; it only prevents the next countdown/start.
            if (_automaticBlinkEnabled != enabled)
                Debug.Log("[PhotoMode] Original automatic blink enabled=" + enabled);
            _automaticBlinkEnabled = enabled;
        }

        public void SetStoryGazeMode(bool active)
        {
            _storyGazeActive = active;
            _preserveAnimatedGaze = false;
            if (active)
            {
                _storyGazeYaw = 0f;
                _storyGazePitch = 0f;
            }
        }

        public void SetStoryGaze(float yaw, float pitch)
        {
            _storyGazeActive = true;
            _preserveAnimatedGaze = false;
            _storyGazeYaw = Mathf.Clamp(yaw, -10f, 10f);
            _storyGazePitch = Mathf.Clamp(pitch, -6f, 6f);
        }

        public void SetStoryMotionGazeMode(bool active)
        {
            // ActorMotion _b clips contain authored humanoid eye muscles.  ADV
            // must suppress photo-mode camera tracking without replacing those
            // muscles with a neutral eye rotation.  Explicit LookTarget remains
            // downstream through CapturedLookAtRuntime.
            _storyGazeActive = active;
            _preserveAnimatedGaze = active;
            if (!active)
            {
                _storyGazeYaw = 0f;
                _storyGazePitch = 0f;
            }
        }

        public void SetCapturedLookAtOwned(bool active)
        {
            _capturedLookAtOwned = active;
        }

        public void SetStoryBlink(float progress)
        {
            _storyBlinkActive = progress >= 0f;
            _storyBlinkProgress = _storyBlinkActive ? Mathf.Clamp01(progress) : 0f;
            _storyBlinkWeight = _storyBlinkActive
                ? Mathf.Clamp01(OriginalBlinkCurve.Evaluate(
                    _storyBlinkProgress * OriginalBlinkDuration) * 0.01f)
                : 0f;
            if (_traceBlink && _storyBlinkActive)
            {
                Debug.Log(string.Format(
                    "[PhotoMode] Original story blink sample: frame={0}, progress={1:R}, curveTime={2:R}, weight={3:R}",
                    Time.frameCount, _storyBlinkProgress,
                    _storyBlinkProgress * OriginalBlinkDuration, _storyBlinkWeight));
            }
        }

        private void LateUpdate()
        {
            UpdateBlink();
            UpdateGaze();
            ApplyCurrentWeights();
        }

        public void InitializeGaze(Transform leftEye, Transform rightEye, Camera lookCamera)
        {
            _leftEye = leftEye;
            _rightEye = rightEye;
            _lookCamera = lookCamera;
            _leftEyeRest = _leftEye == null ? Quaternion.identity : _leftEye.localRotation;
            _rightEyeRest = _rightEye == null ? Quaternion.identity : _rightEye.localRotation;
            Debug.Log(string.Format("[PhotoMode] Eye gaze ready: left={0}, right={1}", _leftEye != null, _rightEye != null));
        }

        private void UpdateBlink()
        {
            _blinkWritePending = false;
            _eyeLimitActive = IsOriginalEyeLimitActive();
            _autoBlinkAllowed = _automaticBlinkEnabled && !_eyeLimitActive && !_storyBlinkActive;
            if (_autoBlinkAllowed)
            {
                _nextBlinkTime -= Time.deltaTime;
            }
            if (_nextBlinkTime <= 0f && _nextBlinkTime != 0f)
            {
                _blinkActive = true;
                _blinkElapsed = 0f;
                _nextBlinkTime = UnityEngine.Random.Range(3f, 6f);
            }

            if (!_blinkActive)
            {
                _blinkWeight = 0f;
                return;
            }

            // VLActorFacialSystem advances currentBlinkTime before evaluating
            // the serialized curve, then clamps normalized time to [0, 1].
            _blinkElapsed += Time.deltaTime;
            float curveTime = Mathf.Clamp01(_blinkElapsed / OriginalBlinkDuration) * OriginalBlinkDuration;
            _blinkWeight = Mathf.Clamp01(OriginalBlinkCurve.Evaluate(curveTime) * 0.01f);
            _blinkWritePending = true;
            if (_traceBlink)
            {
                Debug.Log(string.Format(
                    "[PhotoMode] Original blink sample: frame={0}, delta={1:R}, elapsed={2:R}, curveTime={3:R}, weight={4:R}, eyeLimit={5}, autoAllowed={6}",
                    Time.frameCount, Time.deltaTime, _blinkElapsed, curveTime, _blinkWeight,
                    _eyeLimitActive, _autoBlinkAllowed));
            }
            if (_blinkElapsed >= OriginalBlinkDuration) _blinkActive = false;
        }

        private void UpdateGaze()
        {
            if (_leftEye == null || _rightEye == null || _lookCamera == null) return;
            // The pass192 path drives the mapped humanoid eye muscles in its
            // AnimationScriptPlayable. Do not overwrite those eye transforms
            // with the old passive eye-only LookRotation approximation.
            if (_capturedLookAtOwned || _preserveAnimatedGaze) return;
            Transform frame = _leftEye.parent;
            if (frame == null) return;
            float yaw;
            float pitch;
            if (_storyGazeActive)
            {
                yaw = _storyGazeYaw;
                pitch = _storyGazePitch;
            }
            else
            {
                Vector3 center = (_leftEye.position + _rightEye.position) * 0.5f;
                Vector3 localDirection = frame.InverseTransformDirection((_lookCamera.transform.position - center).normalized);
                // The fixed original GPA frame fits both eye bones at essentially
                // neutral yaw and +0.20 degrees pitch.  A full 9-degree automatic
                // camera solve visibly drives the iris into the sclera boundary,
                // so reserve those larger angles for authored story commands and
                // keep passive camera tracking in the observed soft range.
                yaw = Mathf.Clamp(Mathf.Atan2(localDirection.x, Mathf.Max(0.001f, localDirection.z)) * Mathf.Rad2Deg, -4.5f, 4.5f);
                pitch = Mathf.Clamp(-Mathf.Atan2(localDirection.y, Mathf.Sqrt(localDirection.x * localDirection.x + localDirection.z * localDirection.z)) * Mathf.Rad2Deg, -3f, 3f);
            }
            float response = 1f - Mathf.Exp(-Time.deltaTime * (_storyGazeActive ? 14f : 8f));
            _gazeYaw = Mathf.Lerp(_gazeYaw, yaw, response);
            _gazePitch = Mathf.Lerp(_gazePitch, pitch, response);
            Quaternion offset = Quaternion.Euler(_gazePitch, _gazeYaw, 0f);
            _leftEye.localRotation = Quaternion.Slerp(_leftEye.localRotation, _leftEyeRest * offset, response);
            _rightEye.localRotation = Quaternion.Slerp(_rightEye.localRotation, _rightEyeRest * offset, response);
        }

        public void ApplyCurrentWeights()
        {
            if (!_ready) return;
            int count = _shapeSource.blendShapes.Count;
            float[] weights = new float[count];
            if (_debugShape >= 0 && _debugShape < count)
            {
                weights[_debugShape] = _debugWeight;
            }
            else if (_presetIndex == 0)
            {
                for (int index = 0; index < count; index++) weights[index] = Mathf.Clamp01(_weightDriver.GetWeight(index));
            }
            else
            {
                foreach (WeightedShape shape in Presets[_presetIndex].shapes)
                {
                    if (shape.index >= 0 && shape.index < count) weights[shape.index] = shape.weight;
                }
            }
            ApplyOriginalBlinkWeights(weights);

            ApplyAuthoredFaceCorrection(weights);
            EyeHighlightOffset = CalculateEyeHighlightOffset(weights);
            ApplyEyeHighlightMaterialContract();

            bool changed = false;
            ActiveWeightCount = 0;
            MaximumWeight = 0f;
            for (int index = 0; index < count; index++)
            {
                if (weights[index] > 0.0001f)
                {
                    ActiveWeightCount++;
                    MaximumWeight = Mathf.Max(MaximumWeight, weights[index]);
                }
                if (float.IsNaN(_lastWeights[index]) || Mathf.Abs(weights[index] - _lastWeights[index]) > 0.0001f) changed = true;
            }
            if (TryApplyGpuDeformation(weights, changed)) return;
            changed |= _cpuBlendNeedsRefresh;
            _cpuBlendNeedsRefresh = false;
            if (changed)
            {
                Array.Copy(_baseVertices, _blendVertices, _baseVertices.Length);
                for (int shapeIndex = 0; shapeIndex < count; shapeIndex++)
                {
                    float weight = weights[shapeIndex];
                    if (Mathf.Abs(weight) < 0.0001f) continue;
                    VLFaceBlendShape shape = _shapeSource.blendShapes[shapeIndex];
                    if (shape == null || shape.blendShapeVertices == null) continue;
                    foreach (VLFaceBlendShapeVertex vertex in shape.blendShapeVertices)
                    {
                        if (vertex.vertIndex >= 0 && vertex.vertIndex < _blendVertices.Length)
                            _blendVertices[vertex.vertIndex] += vertex.position * weight;
                    }
                }
                Array.Copy(weights, _lastWeights, count);
            }

            if (!changed && !_boneSkinningReady) return;
            ApplyCustomBoneSkinning();
        }

        private void ApplyCustomBoneSkinning()
        {
            if (!_boneSkinningReady)
            {
                _deformedMesh.vertices = _blendVertices;
                // Blend-shape records contain position deltas only.  Preserve
                // authored normals instead of recalculating split UV/material
                // seams into a visible vertical lighting discontinuity.
                if (_baseNormals != null && _baseNormals.Length == _baseVertices.Length)
                    _deformedMesh.normals = _baseNormals;
                if (_baseTangents != null && _baseTangents.Length == _baseVertices.Length)
                    _deformedMesh.tangents = _baseTangents;
                _deformedMesh.RecalculateBounds();
                _maximumBoneDisplacement = 0f;
                return;
            }

            bool hasBoneMotion = PrepareSkinMatrices();

            // Preserve the authored mesh bit-for-bit at bone rest.  Even a
            // normalized sum of four identity influences can accumulate a few
            // float ULPs; there is no reason to pay that error or CPU cost until
            // at least one eye/tongue matrix actually moves.
            if (!hasBoneMotion)
            {
                _deformedMesh.vertices = _blendVertices;
                if (_baseNormals != null && _baseNormals.Length == _baseVertices.Length)
                    _deformedMesh.normals = _baseNormals;
                if (_baseTangents != null && _baseTangents.Length == _baseVertices.Length)
                    _deformedMesh.tangents = _baseTangents;
                _deformedMesh.RecalculateBounds();
                _maximumBoneDisplacement = 0f;
                return;
            }

            ApplyPreparedBoneSkinning();
        }

        private bool PrepareSkinMatrices()
        {
            Transform root = _shapeSource.bones[0];
            Matrix4x4 meshWorldToLocal = _renderer.transform.worldToLocalMatrix;
            Matrix4x4 headBindToMesh = _shapeSource.bindposes[0].inverse;
            Matrix4x4 meshFromHeadWorld = root.worldToLocalMatrix;
            bool hasBoneMotion = false;
            for (int boneIndex = 0; boneIndex < _skinMatrices.Length; boneIndex++)
            {
                Transform bone = _shapeSource.bones[boneIndex];
                if (bone == null)
                {
                    _skinMatrices[boneIndex] = Matrix4x4.identity;
                    _boneMotion[boneIndex] = false;
                    continue;
                }
                // VLActorFaceModel is a complete linear-blend-skinning source:
                // renderer.worldToLocal * bone.localToWorld * bindpose.  The
                // face MeshRenderer remains in Actor space while Head_Face is
                // rebound below the animated body Head, so the global Head pose
                // must be present in the matrix.  The former virtual-head frame
                // cancelled that transform and only happened to work for the
                // standing bind-like photo poses; seated/common motions left the
                // face floating at its authored world position.
                //
                // Keep the pass175 relative-only expression as an explicit A/B
                // switch for the fixed-camera evidence, not as production.
                if (_legacyRelativeHeadSkinning)
                {
                    Matrix4x4 boneFromHead =
                        meshFromHeadWorld * bone.localToWorldMatrix;
                    _skinMatrices[boneIndex] = headBindToMesh * boneFromHead *
                        _shapeSource.bindposes[boneIndex];
                }
                else
                {
                    _skinMatrices[boneIndex] = meshWorldToLocal *
                        bone.localToWorldMatrix * _shapeSource.bindposes[boneIndex];
                }
                if (IsNearlyIdentity(_skinMatrices[boneIndex]))
                {
                    _skinMatrices[boneIndex] = Matrix4x4.identity;
                    _boneMotion[boneIndex] = false;
                }
                else
                {
                    hasBoneMotion = true;
                    _boneMotion[boneIndex] = true;
                }
            }

            return hasBoneMotion;
        }

        private void ApplyPreparedBoneSkinning()
        {
            const float inverseUnorm16 = 1f / 65535f;
            uint[] packedWeights = _shapeSource.boneWeightAndIndices;
            _maximumBoneDisplacement = 0f;
            for (int vertexIndex = 0; vertexIndex < _blendVertices.Length; vertexIndex++)
            {
                int influenceOffset = vertexIndex * 4;
                bool vertexHasBoneMotion = false;
                for (int influence = 0; influence < 4; influence++)
                {
                    uint packed = packedWeights[influenceOffset + influence];
                    int boneIndex = (int)(packed & 0xFFFFu);
                    if ((packed >> 16) > 0 && boneIndex >= 0 && boneIndex < _boneMotion.Length && _boneMotion[boneIndex])
                    {
                        vertexHasBoneMotion = true;
                        break;
                    }
                }
                if (!vertexHasBoneMotion)
                {
                    _workingVertices[vertexIndex] = _blendVertices[vertexIndex];
                    if (_workingNormals != null) _workingNormals[vertexIndex] = _baseNormals[vertexIndex];
                    if (_workingTangents != null) _workingTangents[vertexIndex] = _baseTangents[vertexIndex];
                    continue;
                }

                Vector3 position = Vector3.zero;
                Vector3 normal = Vector3.zero;
                Vector3 tangent = Vector3.zero;
                float tangentW = _baseTangents != null && vertexIndex < _baseTangents.Length
                    ? _baseTangents[vertexIndex].w : 1f;
                float totalWeight = 0f;
                for (int influence = 0; influence < 4; influence++)
                {
                    uint packed = packedWeights[influenceOffset + influence];
                    float weight = (packed >> 16) * inverseUnorm16;
                    int boneIndex = (int)(packed & 0xFFFFu);
                    if (weight <= 0f || boneIndex < 0 || boneIndex >= _skinMatrices.Length) continue;
                    Matrix4x4 matrix = _skinMatrices[boneIndex];
                    position += matrix.MultiplyPoint3x4(_blendVertices[vertexIndex]) * weight;
                    if (_workingNormals != null)
                        normal += matrix.MultiplyVector(_baseNormals[vertexIndex]) * weight;
                    if (_workingTangents != null)
                    {
                        Vector4 sourceTangent = _baseTangents[vertexIndex];
                        tangent += matrix.MultiplyVector(
                            new Vector3(sourceTangent.x, sourceTangent.y, sourceTangent.z)) * weight;
                    }
                    totalWeight += weight;
                }
                if (totalWeight > 0f)
                {
                    // Packed UNORM16 weights commonly sum to a value a few ULPs
                    // below one.  The original custom-skinning pass normalizes the
                    // accumulated result; without that invariant even an identity
                    // rest pose shrinks the mesh by roughly 0.06 mm.
                    float inverseTotalWeight = 1f / totalWeight;
                    position *= inverseTotalWeight;
                    if (_workingNormals != null) normal *= inverseTotalWeight;
                    if (_workingTangents != null) tangent *= inverseTotalWeight;
                }
                else
                {
                    position = _blendVertices[vertexIndex];
                    if (_workingNormals != null) normal = _baseNormals[vertexIndex];
                    if (_workingTangents != null)
                    {
                        Vector4 sourceTangent = _baseTangents[vertexIndex];
                        tangent = new Vector3(sourceTangent.x, sourceTangent.y, sourceTangent.z);
                    }
                }
                _workingVertices[vertexIndex] = position;
                _maximumBoneDisplacement = Mathf.Max(_maximumBoneDisplacement,
                    Vector3.Distance(position, _blendVertices[vertexIndex]));
                if (_workingNormals != null)
                    _workingNormals[vertexIndex] = normal.sqrMagnitude > 0f ? normal.normalized : _baseNormals[vertexIndex];
                if (_workingTangents != null)
                {
                    Vector3 normalized = tangent.sqrMagnitude > 0f ? tangent.normalized : Vector3.right;
                    _workingTangents[vertexIndex] = new Vector4(normalized.x, normalized.y, normalized.z, tangentW);
                }
            }

            _deformedMesh.vertices = _workingVertices;
            if (_workingNormals != null) _deformedMesh.normals = _workingNormals;
            if (_workingTangents != null) _deformedMesh.tangents = _workingTangents;
            _deformedMesh.RecalculateBounds();
        }

        private static bool IsNearlyIdentity(Matrix4x4 matrix)
        {
            const float tolerance = 1e-5f;
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    float expected = row == column ? 1f : 0f;
                    if (Mathf.Abs(matrix[row, column] - expected) > tolerance) return false;
                }
            }
            return true;
        }

        private void ApplyAuthoredFaceCorrection(float[] weights)
        {
            ViewProfileAngle = 0f;
            ViewProfileWeight = 0f;
            Array.Clear(_faceCorrectionWeights, 0, _faceCorrectionWeights.Length);
            _faceCorrectionYaw = 0f;
            _faceCorrectionPitch = 0f;
            if (_faceCorrection == null || _faceCorrection.curves == null ||
                _faceCorrection.blendShapeIndices == null || _lookCamera == null)
                return;

            Transform target = _faceCorrection.target != null ? _faceCorrection.target : transform;
            Transform poseTarget = _faceCorrectionPoseTarget != null ? _faceCorrectionPoseTarget : target;
            _faceCorrectionSourceLocalEuler = SignedEuler(poseTarget.localEulerAngles);
            _faceCorrectionSourceWorldEuler = SignedEuler(poseTarget.eulerAngles);
            string[] args = Environment.GetCommandLineArgs();
            bool legacyCameraBasis = args.Contains("--legacy-camera-face-correction");
            float yaw;
            float pitch;
            if (legacyCameraBasis)
            {
                Vector3 direction = target.InverseTransformDirection(
                    (_lookCamera.transform.position - target.position).normalized);
                yaw = Mathf.Atan2(direction.x, Mathf.Max(0.001f, direction.z)) * Mathf.Rad2Deg;
                pitch = -Mathf.Atan2(direction.y,
                    Mathf.Sqrt(direction.x * direction.x + direction.z * direction.z)) * Mathf.Rad2Deg;
            }
            else
            {
                // CampusActorFaceCorrection stores a target Transform, a cached
                // Euler vector, and four authored curves.  It does not store a
                // camera.  Drive the curves from the animated head pose rather
                // than from camera parallax; the old camera basis could apply
                // under045 at ~1.0 to a frontal head and lengthen the chin.
                yaw = _faceCorrectionSourceLocalEuler.y;
                pitch = _faceCorrectionSourceLocalEuler.x;
            }
            _faceCorrectionYaw = yaw;
            _faceCorrectionPitch = pitch;
            // The captured photo-mode face has zero weight on shapes 84..87.
            // Keep that verified behavior by default.  The authored pose curves
            // remain available for side/down-pose research through an explicit
            // diagnostic opt-in, while legacy camera-based behavior is retained
            // solely for exact A/B comparisons.
            bool enabled = legacyCameraBasis || args.Contains("--face-angle-correction-on");
            bool disabled = args.Contains("--face-angle-correction-off") || !enabled;
            int count = Mathf.Min(_faceCorrection.curves.Length, _faceCorrection.blendShapeIndices.Length);
            for (int index = 0; index < count; index++)
            {
                int shapeIndex = _faceCorrection.blendShapeIndices[index];
                if (shapeIndex < 0 || shapeIndex >= weights.Length || _faceCorrection.curves[index] == null) continue;
                float angle = index == 0 ? pitch : index == 1 ? Mathf.Abs(yaw) : yaw;
                float correction = disabled ? 0f : Mathf.Clamp01(_faceCorrection.curves[index].Evaluate(angle));
                if (index < _faceCorrectionWeights.Length) _faceCorrectionWeights[index] = correction;
                weights[shapeIndex] = Mathf.Max(weights[shapeIndex], correction);
            }
            if (!enabled && !args.Contains("--face-angle-correction-off")) ApplyViewProfileCorrection(weights);
        }

        private void ResolveViewProfileCorrection()
        {
            _viewProfileSlot = -1;
            if (_faceCorrection == null || _faceCorrection.curves == null ||
                _faceCorrection.blendShapeIndices == null) return;
            int count = Mathf.Min(_faceCorrection.curves.Length, _faceCorrection.blendShapeIndices.Length);
            for (int slot = 0; slot < count; slot++)
            {
                int shape = _faceCorrection.blendShapeIndices[slot];
                if (shape < 0 || shape >= _shapeSource.blendShapes.Count || _faceCorrection.curves[slot] == null) continue;
                VLFaceBlendShape value = _shapeSource.blendShapes[shape];
                if (value != null && value.blendShapeName == "side090")
                {
                    _viewProfileSlot = slot;
                    return;
                }
            }
        }

        internal static float ViewProfileYaw(Transform head, Camera camera, Vector3 center)
        {
            if (head == null || camera == null) return 0f;
            Vector3 view = camera.orthographic ? -camera.transform.forward : camera.transform.position - center;
            float right = Vector3.Dot(view, head.right);
            float forward = Vector3.Dot(view, head.forward);
            if (float.IsNaN(right) || float.IsNaN(forward) || float.IsInfinity(right) || float.IsInfinity(forward) ||
                right * right + forward * forward < 1e-12f) return 0f;
            // Keep the back hemisphere and ignore vertical parallax. Clamping
            // forward to positive would turn every rear view into a side view.
            return Mathf.Abs(Mathf.Atan2(right, forward) * Mathf.Rad2Deg);
        }

        private void ApplyViewProfileCorrection(float[] weights)
        {
            if (!ViewProfileCorrectionEnabled || _viewProfileSlot < 0 || _debugShape >= 0 ||
                _faceCorrectionPoseTarget == null || _lookCamera == null) return;
            // Preserve explicit historical A/B modes. The broad pitch/45-degree
            // correction remains opt-in; this uses only the authored side090
            // shape and its whole delta set, including nose-line retraction.
            ViewProfileAngle = ViewProfileYaw(_faceCorrectionPoseTarget, _lookCamera,
                HasEyeBones ? EyeCenterPosition : _faceCorrectionPoseTarget.position);
            float value = _faceCorrection.curves[_viewProfileSlot].Evaluate(ViewProfileAngle);
            if (float.IsNaN(value) || float.IsInfinity(value)) return;
            ViewProfileWeight = Mathf.Clamp01(value);
            int shape = _faceCorrection.blendShapeIndices[_viewProfileSlot];
            weights[shape] = Mathf.Max(weights[shape], ViewProfileWeight);
            if (_viewProfileSlot < _faceCorrectionWeights.Length) _faceCorrectionWeights[_viewProfileSlot] = ViewProfileWeight;
        }

        public string DiagnosticJson()
        {
            string[] args = Environment.GetCommandLineArgs();
            bool legacyCameraBasis = args.Contains("--legacy-camera-face-correction");
            bool enabled = legacyCameraBasis || args.Contains("--face-angle-correction-on");
            bool viewProfile = !enabled && !args.Contains("--face-angle-correction-off") &&
                ViewProfileCorrectionEnabled && ViewProfileCorrectionAvailable && _debugShape < 0 &&
                _faceCorrectionPoseTarget != null && _lookCamera != null;
            bool disabled = args.Contains("--face-angle-correction-off") || (!enabled && !viewProfile);
            Vector3 leftEyeDelta = _leftEye == null
                ? Vector3.zero
                : SignedEuler((Quaternion.Inverse(_leftEyeRest) * _leftEye.localRotation).eulerAngles);
            Vector3 rightEyeDelta = _rightEye == null
                ? Vector3.zero
                : SignedEuler((Quaternion.Inverse(_rightEyeRest) * _rightEye.localRotation).eulerAngles);
            List<string> active = new List<string>();
            if (_lastWeights != null && _shapeSource != null && _shapeSource.blendShapes != null)
            {
                int count = Mathf.Min(_lastWeights.Length, _shapeSource.blendShapes.Count);
                for (int index = 0; index < count; index++)
                {
                    float weight = _lastWeights[index];
                    if (float.IsNaN(weight) || Mathf.Abs(weight) < 0.0001f) continue;
                    VLFaceBlendShape shape = _shapeSource.blendShapes[index];
                    string name = shape == null || string.IsNullOrEmpty(shape.blendShapeName)
                        ? ("shape-" + index) : shape.blendShapeName;
                    active.Add(string.Format(
                        "{{\"index\":{0},\"name\":\"{1}\",\"weight\":{2}}}",
                        index, name.Replace("\\", "\\\\").Replace("\"", "\\\""), FloatJson(weight)));
                }
            }
            return string.Format(
                "{{\"schema\":\"digital-kotone.face-runtime-diagnostic.v9\",\"correction_disabled\":{0},\"correction_basis\":\"{1}\",\"correction_yaw\":{2},\"correction_pitch\":{3},\"pose_target_local_euler\":[{4},{5},{6}],\"pose_target_world_euler\":[{7},{8},{9}],\"correction_weights\":[{10},{11},{12},{13}],\"gaze_yaw\":{14},\"gaze_pitch\":{15},\"story_gaze_active\":{46},\"preserve_animated_gaze\":{39},\"captured_lookat_owned\":{47},\"left_eye_rest_delta_euler\":[{40},{41},{42}],\"right_eye_rest_delta_euler\":[{43},{44},{45}],\"custom_skinning_ready\":{16},\"custom_skinning_disabled\":{17},\"custom_skinning_bone_count\":{18},\"maximum_bone_displacement_m\":{19},\"blink_contract\":\"VLActorEyeBlinkData/resources.assets/pathID-765125\",\"blink_shape_index\":{20},\"blink_active\":{21},\"blink_elapsed_seconds\":{22},\"blink_wait_remaining_seconds\":{23},\"automatic_blink_enabled\":{24},\"auto_blink_allowed\":{25},\"eye_limit_active\":{26},\"auto_blink_weight\":{27},\"story_blink_weight\":{28},\"effective_blink_weight\":{29},\"eye_limit_shapes_resolved\":{30},\"eye_limit_shape_count\":{31},\"eye_highlight_offset\":{32},\"eye_highlight_material_count\":{33},\"eye_highlight_base_map_st\":[{34},{35},{36},{37}],\"active_weights\":[{38}]}}",
                disabled ? "true" : "false",
                legacyCameraBasis ? "camera-direction-legacy" : viewProfile ? "head-relative-view-side090" : "pose-target-local-euler",
                FloatJson(viewProfile ? ViewProfileAngle : _faceCorrectionYaw), FloatJson(viewProfile ? 0f : _faceCorrectionPitch),
                FloatJson(_faceCorrectionSourceLocalEuler.x), FloatJson(_faceCorrectionSourceLocalEuler.y),
                FloatJson(_faceCorrectionSourceLocalEuler.z), FloatJson(_faceCorrectionSourceWorldEuler.x),
                FloatJson(_faceCorrectionSourceWorldEuler.y), FloatJson(_faceCorrectionSourceWorldEuler.z),
                FloatJson(_faceCorrectionWeights[0]), FloatJson(_faceCorrectionWeights[1]),
                FloatJson(_faceCorrectionWeights[2]), FloatJson(_faceCorrectionWeights[3]),
                FloatJson(_gazeYaw), FloatJson(_gazePitch),
                _boneSkinningReady ? "true" : "false", _boneSkinningDisabled ? "true" : "false",
                _shapeSource == null || _shapeSource.bones == null ? 0 : _shapeSource.bones.Length,
                IsGpuDeformationActive ? "null" : FloatJson(_maximumBoneDisplacement),
                _blinkShapeIndex, _blinkActive ? "true" : "false",
                FloatJson(_blinkElapsed), FloatJson(_nextBlinkTime),
                _automaticBlinkEnabled ? "true" : "false",
                _autoBlinkAllowed ? "true" : "false", _eyeLimitActive ? "true" : "false",
                FloatJson(_blinkWeight), FloatJson(_storyBlinkWeight), FloatJson(BlinkWeight),
                ResolvedEyeLimitShapeCount(), OriginalEyeLimitShapeNames.Length,
                FloatJson(EyeHighlightOffset), _eyeHighlightMaterialIndices.Length,
                FloatJson(_eyeHighlightBaseMapTransform.x), FloatJson(_eyeHighlightBaseMapTransform.y),
                FloatJson(_eyeHighlightBaseMapTransform.z), FloatJson(_eyeHighlightBaseMapTransform.w),
                string.Join(",", active.ToArray()),
                _preserveAnimatedGaze ? "true" : "false",
                FloatJson(leftEyeDelta.x), FloatJson(leftEyeDelta.y), FloatJson(leftEyeDelta.z),
                FloatJson(rightEyeDelta.x), FloatJson(rightEyeDelta.y), FloatJson(rightEyeDelta.z),
                _storyGazeActive ? "true" : "false",
                _capturedLookAtOwned ? "true" : "false");
        }

        private void ResolveOriginalBlinkContract()
        {
            _blinkShapeIndex = -1;
            _eyeLimitShapeIndices = new int[OriginalEyeLimitShapeNames.Length];
            for (int index = 0; index < _eyeLimitShapeIndices.Length; index++)
                _eyeLimitShapeIndices[index] = -1;
            if (_shapeSource == null || _shapeSource.blendShapes == null) return;

            Dictionary<string, int> shapeIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int index = 0; index < _shapeSource.blendShapes.Count; index++)
            {
                VLFaceBlendShape shape = _shapeSource.blendShapes[index];
                if (shape == null || string.IsNullOrEmpty(shape.blendShapeName)) continue;
                if (!shapeIndices.ContainsKey(shape.blendShapeName))
                    shapeIndices.Add(shape.blendShapeName, index);
            }
            int blinkShapeIndex;
            if (shapeIndices.TryGetValue(OriginalBlinkShapeName, out blinkShapeIndex))
                _blinkShapeIndex = blinkShapeIndex;
            for (int index = 0; index < OriginalEyeLimitShapeNames.Length; index++)
            {
                int shapeIndex;
                if (shapeIndices.TryGetValue(OriginalEyeLimitShapeNames[index], out shapeIndex))
                    _eyeLimitShapeIndices[index] = shapeIndex;
            }
            Debug.Log(string.Format(
                "[PhotoMode] Original blink contract ready: shape={0}:{1}, eyeLimits={2}/{3}, duration={4:R}s, wait=Random.Range(3,6)",
                _blinkShapeIndex, OriginalBlinkShapeName, ResolvedEyeLimitShapeCount(),
                OriginalEyeLimitShapeNames.Length, OriginalBlinkDuration));
        }

        private int ResolvedEyeLimitShapeCount()
        {
            if (_eyeLimitShapeIndices == null) return 0;
            int count = 0;
            for (int index = 0; index < _eyeLimitShapeIndices.Length; index++)
                if (_eyeLimitShapeIndices[index] >= 0) count++;
            return count;
        }

        private bool IsOriginalEyeLimitActive()
        {
            if (_eyeLimitShapeIndices == null || _shapeSource == null) return false;
            for (int limitIndex = 0; limitIndex < _eyeLimitShapeIndices.Length; limitIndex++)
            {
                int shapeIndex = _eyeLimitShapeIndices[limitIndex];
                if (shapeIndex < 0) continue;
                float weight = 0f;
                if (_lastWeights != null && shapeIndex < _lastWeights.Length &&
                    !float.IsNaN(_lastWeights[shapeIndex]))
                    weight = _lastWeights[shapeIndex];
                else if (_weightDriver != null)
                    weight = Mathf.Clamp01(_weightDriver.GetWeight(shapeIndex));
                if (weight >= OriginalEyeLimitThresholds[limitIndex]) return true;
            }
            return false;
        }

        private void ApplyOriginalBlinkWeights(float[] weights)
        {
            if (weights == null || _blinkShapeIndex < 0 || _blinkShapeIndex >= weights.Length) return;
            bool ownsBlink = _blinkWritePending || _blinkActive || _blinkWeight > 0.0001f ||
                _storyBlinkActive;
            if (!ownsBlink) return;

            float blinkWeight = Mathf.Clamp01(Mathf.Max(_blinkWeight, _storyBlinkWeight));
            float openFactor = 1f - blinkWeight;
            for (int limitIndex = 0; limitIndex < _eyeLimitShapeIndices.Length; limitIndex++)
            {
                int shapeIndex = _eyeLimitShapeIndices[limitIndex];
                if (shapeIndex < 0 || shapeIndex >= weights.Length || shapeIndex == _blinkShapeIndex) continue;
                weights[shapeIndex] *= openFactor;
            }
            // SetBlinkBlendShapeWeight overwrites the blink shape; it does not
            // max-blend it with the current facial motion.
            weights[_blinkShapeIndex] = blinkWeight;
        }

        private static AnimationCurve BuildOriginalBlinkCurve()
        {
            AnimationCurve curve = new AnimationCurve(
                new Keyframe(0f, 0f, -0.00010456358f, -0.00010456358f),
                new Keyframe(0.033333335f, 39f, 1499.99976f, 1500f),
                new Keyframe(0.06666667f, 100f, 0.0000662463f, 0.00000000000266454f),
                new Keyframe(0.1f, 100f, 0.00000000000114194f, 0.00273792283f),
                new Keyframe(0.13333334f, 45f, -1230.5437f, -1230.54761f),
                new Keyframe(0.16666667f, 17.9636402f, -615.274292f, -615.273743f),
                new Keyframe(0.2f, 3.98181963f, -269.455566f, -269.454987f),
                new Keyframe(0.233333334f, 0f, -0.0002300275f, -0.0002300275f));
            curve.preWrapMode = WrapMode.ClampForever;
            curve.postWrapMode = WrapMode.ClampForever;
            return curve;
        }

        private static Vector3 SignedEuler(Vector3 value)
        {
            return new Vector3(
                Mathf.DeltaAngle(0f, value.x),
                Mathf.DeltaAngle(0f, value.y),
                Mathf.DeltaAngle(0f, value.z));
        }

        private static string FloatJson(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private float CalculateEyeHighlightOffset(float[] weights)
        {
            if (_eyeHighlight == null || _eyeHighlight.blendShapes == null) return 0f;
            float result = 0f;
            foreach (EyeHighlightBlendShape value in _eyeHighlight.blendShapes)
            {
                if (value == null || value.index < 0 || value.index >= weights.Length) continue;
                result += weights[value.index] * value.value;
            }
            return result;
        }

        private void ResolveEyeHighlightMaterialContract()
        {
            if (_renderer == null)
            {
                _eyeHighlightMaterialIndices = new int[0];
                return;
            }
            Material[] materials = _renderer.sharedMaterials;
            List<int> indices = new List<int>();
            for (int index = 0; index < materials.Length; index++)
            {
                Material material = materials[index];
                if (material == null) continue;
                bool namedEyeHighlight = material.name.StartsWith(
                    "m_ehl", StringComparison.OrdinalIgnoreCase);
                bool typedEyeHighlight = material.HasProperty("_ShaderType") &&
                    Mathf.Abs(material.GetFloat("_ShaderType") - 5f) < 0.25f;
                if (namedEyeHighlight || typedEyeHighlight) indices.Add(index);
            }
            _eyeHighlightMaterialIndices = indices.ToArray();
            _eyeHighlightPropertyBlock = new MaterialPropertyBlock();
            Debug.Log(string.Format(
                "[PhotoMode] Original eye-highlight material contract: renderer={0}, indices=[{1}], property=_BaseMap_ST",
                _renderer.name, string.Join(",", _eyeHighlightMaterialIndices.Select(value => value.ToString()).ToArray())));
        }

        private void ApplyEyeHighlightMaterialContract()
        {
            if (_renderer == null || _eyeHighlightPropertyBlock == null ||
                _eyeHighlightMaterialIndices.Length == 0) return;

            // CampusActorController.UpdateHighLightMaterial writes this literal
            // per-material vector to m_ehl after summing the normalized facial
            // weights in CampusActorEyeHighlight.GetHighlightOffset. Ghidra's
            // 64-bit uStack_54 assignment stores float bits (1, 0), not (1, 1):
            // the exact vector is scale (1,1), U offset 0, dynamic V offset.
            _eyeHighlightBaseMapTransform = new Vector4(1f, 1f, 0f, EyeHighlightOffset);
            foreach (int materialIndex in _eyeHighlightMaterialIndices)
            {
                _renderer.GetPropertyBlock(_eyeHighlightPropertyBlock, materialIndex);
                _eyeHighlightPropertyBlock.SetVector("_BaseMap_ST", _eyeHighlightBaseMapTransform);
                _renderer.SetPropertyBlock(_eyeHighlightPropertyBlock, materialIndex);
                _eyeHighlightPropertyBlock.Clear();
            }
        }

        private void ForceRefresh()
        {
            if (_lastWeights == null) return;
            for (int index = 0; index < _lastWeights.Length; index++) _lastWeights[index] = float.NaN;
            ApplyCurrentWeights();
        }

        private void OnDestroy()
        {
            ReleaseGpuDeformation();
            if (_deformedMesh != null) DestroyImmediate(_deformedMesh);
        }

        private sealed class ExpressionPreset
        {
            public readonly string name;
            public readonly WeightedShape[] shapes;
            public ExpressionPreset(string value, params WeightedShape[] weightedShapes)
            {
                name = value;
                shapes = weightedShapes ?? new WeightedShape[0];
            }
        }

        private struct WeightedShape
        {
            public readonly int index;
            public readonly float weight;
            public WeightedShape(int shapeIndex, float shapeWeight)
            {
                index = shapeIndex;
                weight = shapeWeight;
            }
        }
    }
}
