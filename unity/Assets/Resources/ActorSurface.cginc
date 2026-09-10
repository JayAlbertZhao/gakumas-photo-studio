#ifndef PHOTO_STUDIO_ACTOR_SURFACE
#define PHOTO_STUDIO_ACTOR_SURFACE
#include "UnityCG.cginc"
#include "Lighting.cginc"
#include "AutoLight.cginc"

sampler2D _MainTex, _ShadeTex, _DefTex, _RampTex, _LayerTex;
sampler2D _HighlightTex, _RampAddTex, _BumpMap, _AnisotropicMap;
sampler2D _ReflectionSphereMap, _EmissionMap;
sampler2D _FaceDecalAtlas;
sampler2D _CapturedActorShadowTex;
// The GPA sampler contract is ComparisonFunc=GREATER. Unity does
// not expose that state for this color-backed R16 reconstruction;
// the explicit kernel below is validated against the captured frame.
samplerCUBE _ActorEnvironmentCube;
samplerCUBE _ActorEyeEnvironmentCube;
UNITY_DECLARE_TEX2DARRAY(_ActorEnvironmentArray);
// Both captured arrays are built with bilinear/clamp sampling. Share that
// state so the optional matcap plus ForwardBase screen shadows stay within
// the D3D11 limit; the eye payload is only loaded with the actor environment.
UNITY_DECLARE_TEX2DARRAY_NOSAMPLER(_ActorEyeEnvironmentArray);
float4 _MainTex_ST;
float4 _BaseMap_ST;
float4 _ActorTextureFrame;
float4 _Color, _ActorColor, _DefValue, _SpecularThreshold;
float4 _RampAddColor, _RimColor, _EmissionColor;
float _ShaderType, _CapturedType1Variant, _EnableLayer, _LayerWeight, _VertexColor;
float _DisableDefMap;
float4 _ActorMatcapParameters; // offset, smoothness scale, shade strength
float4 _ActorLightingScales; // GI, additional diffuse, additional specular
float4 _ActorKeyColor, _ActorRimColor, _ActorEyeHighlightColor;
int _ActorAdditionalLightCount;
float4 _ActorAdditionalPositions[8]; // world position, inverse range squared
float4 _ActorAdditionalColors[8];
float4 _ActorAdditionalDirections[8]; // direction, cos(outer half angle); -1 for point
float4 _ActorAdditionalSpots[8]; // cos(inner half angle)
float _FaceDebugMode;
float _CapturedDiffuseBlend;
float _CapturedDirectScale;
float _CapturedAmbientScale;
float _CapturedActorTextureLodBias;
float _CapturedActorOutputScale;
float _CapturedType4DebugStage;
float _CapturedType4RampFlip;
float _CapturedType4DiffuseF0;
float _CapturedType4DefinitionVisibility;
float _CapturedType4OutputScale;
float _CapturedType1OutputScale;
float _CapturedType1BodyOutputScale;
float _CapturedType1HairOutputScale;
float _CapturedType1DebugStage;
float _CapturedType1DebugVariant;
float _CapturedType5OutputScale;
float _UseCapturedDirectSpecular;
float4 _CapturedLightDirection, _CapturedLightColor;
float4 _CapturedShadeTint, _CapturedShadeAdditive;
float4 _CapturedCameraUp;
float _UseCapturedReceiverNormal;
float4 _CapturedRimDirection, _CapturedRimViewDirection, _CapturedRimParameters;
float _UseExactViewRimBasis;
float4 _CapturedSH0, _CapturedSH1, _CapturedSH2, _CapturedSH3;
float4 _CapturedSH4, _CapturedSH5, _CapturedSH6;
float _UseBump, _UseAnisotropic, _UseReflection, _UseEmission;
float _BumpScale, _AnisotropicScale, _UseAlphaClip, _Cutoff;
float _SrcBlend, _DstBlend;
float _ActorEnvironmentIntensity;
float _CapturedSkinSaturation;
float4 _CapturedReflectionColor, _CapturedEyeReflectionColor;
float4 _ReflectionSphereMap_HDR;
float _CapturedEyeCubeTransformMode;
float _CapturedActorCubeTransformMode;
float _UseCapturedEnvironmentBasis;
float _UseCapturedEyeEnvironmentArray;
float _UseCapturedActorEnvironmentArray;
float _UseCapturedType1ActorEnvironmentArray;
float4 _HeadDirection, _HeadUpDirection, _HeadRightDirection, _HeadPosition;
float4 _HairFadeParameters;
float4 _ActorFillDirection, _ActorFillColor;
float4 _ActorRimLightDirection, _ActorRimLightColor;
float4x4 _CapturedActorWorldToShadow;
float4x4 _FaceDecalWorldToDecal[8];
float4 _FaceDecalUvScaleBias[8];
float4 _FaceDecalFade[8];
float _FaceDecalCount;
float4 _CapturedActorShadowTexelSize;
float _UseCapturedActorShadow;
float _UseExactCapturedActorShadowMatrix;
float _CapturedActorShadowStrength;
float _CapturedActorShadowUseOffset;
float _CapturedActorFacePartsShadowStrength;
float4 _WardrobeScaleCorrection;

// The captured D3D cube payload is byte-exact, but Unity and D3D
// may disagree on the direction-to-face convention.  Enumerate all
// six axis permutations and eight sign combinations in one build;
// exactenvironment4 replay selects the coordinate contract by
// correlation rather than by visual guesswork. mode = perm*8+sign.
float3 TransformCapturedCubeDirection(float3 direction, float transformMode)
{
    int mode = clamp((int)round(transformMode), 0, 47);
    int permutation = mode / 8;
    int signCode = mode - permutation * 8;
    float3 permuted = direction.xyz;
    if (permutation == 1) permuted = direction.xzy;
    else if (permutation == 2) permuted = direction.yxz;
    else if (permutation == 3) permuted = direction.yzx;
    else if (permutation == 4) permuted = direction.zxy;
    else if (permutation == 5) permuted = direction.zyx;
    float3 signs = float3(
        (signCode % 2) != 0 ? -1.0 : 1.0,
        ((signCode / 2) % 2) != 0 ? -1.0 : 1.0,
        ((signCode / 4) % 2) != 0 ? -1.0 : 1.0);
    return permuted * signs;
}

float3 TransformCapturedEyeCubeDirection(float3 direction)
{
    return TransformCapturedCubeDirection(direction, _CapturedEyeCubeTransformMode);
}

float3 SampleCapturedEyeEnvironmentArray(float3 direction, float mip)
{
    float3 absoluteDirection = abs(direction);
    float face = 0.0;
    float2 faceCoordinate = 0.0;
    float majorAxis = absoluteDirection.x;
    if (absoluteDirection.x >= absoluteDirection.y &&
        absoluteDirection.x >= absoluteDirection.z)
    {
        if (direction.x >= 0.0)
        {
            face = 0.0;
            faceCoordinate = float2(-direction.z, -direction.y);
        }
        else
        {
            face = 1.0;
            faceCoordinate = float2(direction.z, -direction.y);
        }
    }
    else if (absoluteDirection.y >= absoluteDirection.z)
    {
        majorAxis = absoluteDirection.y;
        if (direction.y >= 0.0)
        {
            face = 2.0;
            faceCoordinate = float2(direction.x, direction.z);
        }
        else
        {
            face = 3.0;
            faceCoordinate = float2(direction.x, -direction.z);
        }
    }
    else
    {
        majorAxis = absoluteDirection.z;
        if (direction.z >= 0.0)
        {
            face = 4.0;
            faceCoordinate = float2(direction.x, -direction.y);
        }
        else
        {
            face = 5.0;
            faceCoordinate = float2(-direction.x, -direction.y);
        }
    }
    float2 uv = faceCoordinate / max(majorAxis, 1e-6) * 0.5 + 0.5;
    return UNITY_SAMPLE_TEX2DARRAY_SAMPLER_LOD(
        _ActorEyeEnvironmentArray, _ActorEnvironmentArray, float3(uv, face), mip).rgb;
}

float3 SampleCapturedActorEnvironmentArray(float3 direction, float mip)
{
    float3 absoluteDirection = abs(direction);
    float face = 0.0;
    float2 faceCoordinate = 0.0;
    float majorAxis = absoluteDirection.x;
    if (absoluteDirection.x >= absoluteDirection.y &&
        absoluteDirection.x >= absoluteDirection.z)
    {
        if (direction.x >= 0.0)
        {
            face = 0.0;
            faceCoordinate = float2(-direction.z, -direction.y);
        }
        else
        {
            face = 1.0;
            faceCoordinate = float2(direction.z, -direction.y);
        }
    }
    else if (absoluteDirection.y >= absoluteDirection.z)
    {
        majorAxis = absoluteDirection.y;
        if (direction.y >= 0.0)
        {
            face = 2.0;
            faceCoordinate = float2(direction.x, direction.z);
        }
        else
        {
            face = 3.0;
            faceCoordinate = float2(direction.x, -direction.z);
        }
    }
    else
    {
        majorAxis = absoluteDirection.z;
        if (direction.z >= 0.0)
        {
            face = 4.0;
            faceCoordinate = float2(direction.x, -direction.y);
        }
        else
        {
            face = 5.0;
            faceCoordinate = float2(-direction.x, -direction.y);
        }
    }
    float2 uv = faceCoordinate / max(majorAxis, 1e-6) * 0.5 + 0.5;
    return UNITY_SAMPLE_TEX2DARRAY_LOD(
        _ActorEnvironmentArray, float3(uv, face), mip).rgb;
}

struct appdata
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    float4 tangent : TANGENT;
    float2 uv : TEXCOORD0;
    float2 uv1 : TEXCOORD1;
    float4 color : COLOR;
};

struct v2f
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
    float3 worldPosition : TEXCOORD1;
    float3 worldNormal : TEXCOORD2;
    float3 worldTangent : TEXCOORD3;
    float3 worldBitangent : TEXCOORD4;
    float4 color : COLOR;
    SHADOW_COORDS(5)
    float packedRampAdd : TEXCOORD6;
    float packedRimMask : TEXCOORD7;
    // VS 54E7B147 builds type-9 TEXCOORD4 from the skinned
    // object-space normal and the runtime head-bone basis.
    float3 objectNormal : TEXCOORD8;
    float2 layerUv : TEXCOORD9;
};

v2f vert(appdata input)
{
    v2f output;
    input.vertex.xyz *= _WardrobeScaleCorrection.xyz;
    input.normal.xz /= max(_WardrobeScaleCorrection.xz, 0.0001);
    output.pos = UnityObjectToClipPos(input.vertex);
    output.uv = TRANSFORM_TEX(input.uv, _MainTex);
    output.layerUv = input.uv1 * float2(0.5, 1.0);
    output.worldPosition = mul(unity_ObjectToWorld, input.vertex).xyz;
    output.worldNormal = UnityObjectToWorldNormal(input.normal);
    output.worldTangent = UnityObjectToWorldDir(input.tangent.xyz);
    output.worldBitangent = cross(output.worldNormal, output.worldTangent) * input.tangent.w * unity_WorldTransformParams.w;
    output.color = input.color;
    // Bound VS 54E7B147 decodes two normalized 4-bit values from
    // every vertex-color byte. Hair PS consumes the low nibble of
    // COLOR.g as RampAdd Y; every Actor PS consumes the high nibble
    // of COLOR.a as the authored rim mask.
    float4 highNibble = floor(input.color * 15.9375 + 0.03125);
    float4 lowNibble = input.color * 255.0 - highNibble * 16.0;
    output.packedRampAdd = lowNibble.g / 15.0;
    output.packedRimMask = highNibble.a / 15.0;
    output.objectNormal = normalize(input.normal);
    TRANSFER_SHADOW(output);
    return output;
}

float3 ActorNormal(v2f input)
{
    float3 n = normalize(input.worldNormal);
    if (_UseBump > 0.5)
    {
        float3 tangentNormal = UnpackNormal(tex2Dbias(
            _BumpMap, float4(input.uv, 0.0, _CapturedActorTextureLodBias)));
        tangentNormal.xy *= _BumpScale;
        float3 t = normalize(input.worldTangent);
        float3 b = normalize(input.worldBitangent);
        n = normalize(t * tangentNormal.x + b * tangentNormal.y + n * tangentNormal.z);
    }
    return n;
}

float3 SphereReflection(float3 worldNormal, float3 viewDirection)
{
    // A matcap projects the normal into a camera-facing frame; it does not
    // sample the reflected cubemap direction. Orthographic rays are parallel.
    float3 view = unity_OrthoParams.w > 0.5
        ? normalize(UNITY_MATRIX_V[2].xyz) : viewDirection;
    float3 horizontal = normalize(cross(view, UNITY_MATRIX_V[1].xyz));
    float3 vertical = normalize(cross(horizontal, view));
    float2 uv = float2(dot(worldNormal, horizontal), dot(worldNormal, vertical)) * 0.5 + 0.5;
    float4 sample = tex2Dbias(_ReflectionSphereMap,
        float4(uv, 0.0, _CapturedActorTextureLodBias + 1.0));
    float encodedScale = max(1.0 + _ReflectionSphereMap_HDR.w * (sample.a - 1.0), 0.0);
    return sample.rgb * (_ReflectionSphereMap_HDR.x * pow(encodedScale, _ReflectionSphereMap_HDR.y));
}

float AnisotropicHighlight(float3 tangent, float3 bitangent, float3 halfDirection, float3 anisotropic)
{
    float3 strand = normalize(tangent + bitangent * ((anisotropic.r * 2.0 - 1.0) * _AnisotropicScale));
    float tangentDot = dot(strand, halfDirection);
    float sinTheta = sqrt(saturate(1.0 - tangentDot * tangentDot));
    float broad = pow(sinTheta, 10.0);
    float narrow = pow(sinTheta, 42.0);
    return lerp(broad, narrow, saturate(anisotropic.g)) * saturate(anisotropic.b + 0.35);
}

float CapturedShadowComparison(float2 uv, float receiverDepth)
{
    float mapDepth = tex2D(_CapturedActorShadowTex, uv).r;
    #if defined(UNITY_REVERSED_Z)
        return receiverDepth > mapDepth ? 1.0 : 0.0;
    #else
        return receiverDepth < mapDepth ? 1.0 : 0.0;
    #endif
}

float CapturedActorShadow(float3 worldPosition, float definitionRed)
{
    // Disabled previews need no shadow matrix or texture dimensions. Avoid
    // evaluating an unset projection/texel division and then lerping its NaN.
    if (_UseCapturedActorShadow <= 0.0) return 1.0;
    float4 clip = mul(_CapturedActorWorldToShadow, float4(worldPosition, 1.0));
    float3 projected = clip.xyz / clip.w;
    float2 uv = projected.xy * 0.5 + 0.5;
    // Camera.RenderWithShader writes the render target through Unity's
    // D3D render-texture convention; sampling it in a forward pass needs
    // the matching vertical flip.
    uv.y = 1.0 - uv.y;
    // The exact GPA contract is already world-to-UV/depth; the
    // fallback camera matrix above is world-to-clip.
    uv = lerp(uv, clip.xy, saturate(_UseExactCapturedActorShadowMatrix));
    projected.z = lerp(projected.z, clip.z, saturate(_UseExactCapturedActorShadowMatrix));
    float inBounds = step(0.0, uv.x) * step(uv.x, 1.0) *
                     step(0.0, uv.y) * step(uv.y, 1.0);
    if (inBounds < 0.5) return 1.0;

    // Four half-texel-offset bilinear comparison samples collapse to
    // a separable 3x3 kernel. Its outer weights depend on the receiver's
    // fractional texel position; fixed binomial weights only match at
    // texel centres and make moving shadow edges advance in steps.
    // Sample texel centres explicitly: the depth texture is point-filtered.
    float2 texel = _CapturedActorShadowTexelSize.xy;
    float2 grid = uv / texel;
    float2 fraction = frac(grid);
    float2 centreUv = (floor(grid) + 0.5) * texel;
    float3 weightX = float3(1.0 - fraction.x, 1.0, fraction.x) * 0.5;
    float3 weightY = float3(1.0 - fraction.y, 1.0, fraction.y) * 0.5;
    float filtered = 0.0;
    filtered += CapturedShadowComparison(centreUv + texel * float2(-1,-1), projected.z) * weightX.x * weightY.x;
    filtered += CapturedShadowComparison(centreUv + texel * float2( 0,-1), projected.z) * weightX.y * weightY.x;
    filtered += CapturedShadowComparison(centreUv + texel * float2( 1,-1), projected.z) * weightX.z * weightY.x;
    filtered += CapturedShadowComparison(centreUv + texel * float2(-1, 0), projected.z) * weightX.x * weightY.y;
    filtered += CapturedShadowComparison(centreUv, projected.z) * weightX.y * weightY.y;
    filtered += CapturedShadowComparison(centreUv + texel * float2( 1, 0), projected.z) * weightX.z * weightY.y;
    filtered += CapturedShadowComparison(centreUv + texel * float2(-1, 1), projected.z) * weightX.x * weightY.z;
    filtered += CapturedShadowComparison(centreUv + texel * float2( 0, 1), projected.z) * weightX.y * weightY.z;
    filtered += CapturedShadowComparison(centreUv + texel * float2( 1, 1), projected.z) * weightX.z * weightY.z;

    // Literal receiver tail shared by the bound type 0/1/4/6/9
    // Actor pixel shaders. CB0[144] is
    // (shadowStrength, useOffset, 1.69926143, -4.93700218).
    // The four hardware-bilinear half-texel comparison taps above
    // are equivalent to this phase-aware 3x3 kernel. The
    // distance term fades that filtered result to fully lit. When
    // useOffset is enabled, the positive half of the authored Definition.R
    // response is added before strength. It is independent of the central
    // shadow-map comparison: shaded material regions can still be lifted.
    float3 cameraDelta = worldPosition - _WorldSpaceCameraPos.xyz;
    float distanceFade = saturate(
        dot(cameraDelta, cameraDelta) * 1.6992614269256592 -
        4.937002182006836);
    distanceFade *= distanceFade;
    filtered = lerp(filtered, 1.0, distanceFade);
    float authoredOffset = max(definitionRed * 2.0 - 1.0, 0.0);
    float receiver = authoredOffset * saturate(_CapturedActorShadowUseOffset) + filtered;
    return lerp(1.0, receiver, saturate(_CapturedActorShadowStrength));
}

float3 CapturedActorSH(float3 normal)
{
    // Literal instruction contract from bound PS 717B/7372/023B:
    // linear terms use float4(n,1), quadratic basis is
    // (ny*nx, nz*ny, nz*nz, nx*nz), followed by nx^2-ny^2.
    float4 n = float4(normal, 1.0);
    float4 quadratic = normal.yzzx * normal.xyzz;
    float difference = normal.x * normal.x - normal.y * normal.y;
    float3 firstOrder = float3(dot(_CapturedSH0, n), dot(_CapturedSH1, n), dot(_CapturedSH2, n));
    float3 secondOrder = float3(dot(_CapturedSH3, quadratic),
                                dot(_CapturedSH4, quadratic),
                                dot(_CapturedSH5, quadratic));
    return max(firstOrder + secondOrder + _CapturedSH6.rgb * difference, 0.0);
}

float3 ApplyOriginalFaceDecals(float3 surfaceColor, float3 worldPosition)
{
    // URP 14's projector contract is recovered literally from
    // DecalUpdateCachedSystem + ShaderPassDecal: worldToDecal
    // includes the -90 degree X basis conversion, size/pivot volume,
    // clips a unit cube, and maps decal-space XZ into atlas UV.
    [unroll]
    for (int index = 0; index < 8; index++)
    {
        if (index >= (int)_FaceDecalCount) break;
        float3 positionDS = mul(
            _FaceDecalWorldToDecal[index],
            float4(worldPosition, 1.0)).xyz;
        float inside = step(max(abs(positionDS.x),
            max(abs(positionDS.y), abs(positionDS.z))), 0.5);
        float4 uvContract = _FaceDecalUvScaleBias[index];
        float2 uv = (positionDS.xz + 0.5) * uvContract.xy + uvContract.zw;
        float4 decal = tex2D(_FaceDecalAtlas, uv);
        float fade = saturate(_FaceDecalFade[index].x);
        float alpha = decal.a * fade * inside;
        // m_fdc has _ALPHAMODULATE_ON. URP writes
        // lerp(white, albedo, textureAlpha) into the DBuffer and
        // then composites with source alpha.
        float3 alphaModulated = lerp(1.0.xxx, decal.rgb, decal.a);
        surfaceColor = surfaceColor * (1.0 - alpha) + alphaModulated * alpha;
    }
    return surfaceColor;
}

float4 frag(v2f input, float facing : VFACE) : SV_Target
{
    // Opaque Campus actor variants use ActorController.color for
    // timeline fades without changing their authored material
    // blend/depth contract.  Preserve that shape with a stable
    // screen-space coverage fade; alpha zero produces no actor
    // colour, depth, ActorData, or shadow sample.
    float actorFadeNoise = frac(52.9829189 * frac(
        dot(floor(input.pos.xy), float2(0.06711056, 0.00583715))));
    clip(_ActorColor.a - actorFadeNoise);
    // Every bound Actor variant forms r0.x = CB0[5].x - 1 before
    // sampling its authored Base/Shade/Definition maps. CB0[5].x
    // is zero in the captured photo frame, hence a literal -1 mip
    // bias. This matters at the local half-resolution target: the
    // unbiased path blends away the narrow dark strokes authored
    // into hair Shade/Definition maps and reads visually as a
    // missing outline plus a pale albedo shift.
    bool isEyeHighlight = abs(_ShaderType - 5.0) < 0.25;
    float2 actorUv = isEyeHighlight
        ? input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw
        : input.uv;
    if (_ActorTextureFrame.x > 0.0 && _ActorTextureFrame.y > 0.0)
        actorUv = actorUv * _ActorTextureFrame.xy + _ActorTextureFrame.zw;
    float4 actorTextureCoordinate = float4(
        actorUv, 0.0, _CapturedActorTextureLodBias);
    float4 rawBaseSample = tex2Dbias(_MainTex, actorTextureCoordinate);
    // Material RGB tints the complete ramped/saturated surface, not just
    // BaseMap. Keep opacity separate so clipping and blending are unchanged.
    float4 baseSample = rawBaseSample;
    baseSample.a *= _Color.a;
    if (_UseAlphaClip > 0.5)
    {
        float alphaWidth = max(fwidth(baseSample.a), 0.0001);
        clip((baseSample.a - _Cutoff) / alphaWidth + 0.5);
    }

    bool isEye = abs(_ShaderType - 4.0) < 0.25;
    bool isTypeOne = abs(_ShaderType - 1.0) < 0.25;
    bool isFace = abs(_ShaderType - 9.0) < 0.25 || abs(_ShaderType - 6.0) < 0.25 || abs(_ShaderType - 3.0) < 0.25;
    bool isSkin = abs(_ShaderType - 9.0) < 0.25;
    bool isHair = abs(_ShaderType - 8.0) < 0.25;
    bool isFaceCap = abs(_ShaderType - 6.0) < 0.25;

    // Bound VS 54E7B147 repacks source COLOR into eight normalized
    // 4-bit channels. RGB is not an albedo tint (using it as one
    // turns the fktn meshes red/black because it contains packed data).
    float vertexDefinition = lerp(0.5, input.packedRampAdd, saturate(_VertexColor));
    float4 shadeSample = tex2Dbias(_ShadeTex, actorTextureCoordinate);
    float4 definition = _DisableDefMap > 0.5 ? _DefValue : tex2Dbias(_DefTex, actorTextureCoordinate);
    float4 layer = tex2Dbias(_LayerTex, float4(input.layerUv, 0.0, _CapturedActorTextureLodBias - 1.0));
    if (isEye && _CapturedType4DebugStage > 5.5 &&
        _CapturedType4DebugStage < 6.5)
        return rawBaseSample;
    if (isEye && _CapturedType4DebugStage > 6.5 &&
        _CapturedType4DebugStage < 7.5)
        return shadeSample;
    if (isEye && _CapturedType4DebugStage > 7.5 &&
        _CapturedType4DebugStage < 8.5)
        return definition.xzyw;
    if (isEye && _CapturedType4DebugStage > 16.5 &&
        _CapturedType4DebugStage < 17.5)
        return definition.aaaa;
    bool isTypeZeroDebug = abs(_ShaderType) < 0.25;
    // Raw projected material probes intentionally cover every Actor
    // type.  Matching GPA replay probes sample the same t0/t1/t4
    // resources for all active variants, so restricting these modes
    // to type 0 hid whether hair detail was lost before or after the
    // reconstructed lighting equation.  Production mode is zero and
    // is unaffected by these diagnostic returns.
    if (_FaceDebugMode > 12.5 && _FaceDebugMode < 13.5)
        return float4(definition.rgb, 1.0);
    if (_FaceDebugMode > 13.5 && _FaceDebugMode < 14.5)
        return float4(baseSample.rgb * _Color.rgb, 1.0);
    if (_FaceDebugMode > 14.5 && _FaceDebugMode < 15.5)
        return float4(shadeSample.rgb, 1.0);

    // Raw authored inputs, before the reconstructed layer equation.
    if (isSkin && _FaceDebugMode > 0.5 && _FaceDebugMode < 1.5)
        return float4(baseSample.rgb * _Color.rgb, 1.0);

    if (_EnableLayer > 0.5)
    {
        // _EnableLayerMap selects the compiled face-layer path, while
        // _LayerWeight is the runtime blend.  fktn's base material ships
        // enabled with weight 0; treating texture alpha as a fallback weight
        // painted the yellow right half of fce_lyr over the face and produced
        // the conspicuous centre seam.
        float layerMask = saturate(layer.a * _LayerWeight);
        baseSample.rgb = lerp(baseSample.rgb, layer.rgb, layerMask);
        float4 layerDefinition = tex2Dbias(_LayerTex,
            float4(input.layerUv + float2(0.5, 0.0), 0.0, _CapturedActorTextureLodBias - 1.0));
        definition = lerp(definition, layerDefinition, layerMask);
    }

    // All captured Actor variants receive SV_IsFrontFace and flip
    // the interpolated normal for back-facing fragments before SH,
    // ramp, specular and rim evaluation. This is observable on the
    // no-cull type-1 cat ears, hair cards and body ornaments.
    float faceSign = facing >= 0.0 ? 1.0 : -1.0;
    float3 n = ActorNormal(input) * faceSign;
    // Active actor variants 717B09C5/73721FD0/023B1567 all read the
    // normalized key direction from captured CB0[148].xyz.  Keep a
    // Unity-light fallback for material preview, but use the archived
    // photo-mode direction in the application.
    float capturedLightValid = step(0.25, dot(_CapturedLightDirection.xyz, _CapturedLightDirection.xyz));
    float3 l = normalize(lerp(UnityWorldSpaceLightDir(input.worldPosition), _CapturedLightDirection.xyz, capturedLightValid));
    float3 rawViewDirection = UnityWorldSpaceViewDir(input.worldPosition);
    float3 v = normalize(rawViewDirection);
    // PS 5C07/717B/7372 constructs a camera-facing receiver basis
    // from the per-pixel view direction and the camera-up column,
    // then evaluates ramp/direct BRDF terms with that transformed
    // normal (r6). Environment reflection and rim keep the ordinary
    // world normal. The legacy diagnostic can select a world normal;
    // the story light-space switch belongs only to ramp evaluation.
    float3 receiverBasisX = cross(v, normalize(_CapturedCameraUp.xyz));
    float3 receiverBasisY = cross(receiverBasisX, v);
    float3 capturedReceiverNormal = float3(
        dot(receiverBasisX, n), dot(receiverBasisY, n), dot(v, n));
    if (isTypeZeroDebug && _FaceDebugMode > 28.5 && _FaceDebugMode < 29.5)
        return float4(max(capturedReceiverNormal, 0.0), 1.0);
    if (isTypeZeroDebug && _FaceDebugMode > 29.5 && _FaceDebugMode < 30.5)
        return float4(saturate(dot(capturedReceiverNormal, l)).xxx, 1.0);
    float3 receiverNormal = lerp(
        n, capturedReceiverNormal, saturate(_UseCapturedReceiverNormal));
    if (isEye && _CapturedType4DebugStage > 15.5 &&
        _CapturedType4DebugStage < 16.5)
        return float4(max(receiverNormal, 0.0) * baseSample.a, baseSample.a);
    // The active Actor DXBC forms its highlight/direct-specular half
    // vector from CB0[148].xyz + (0,0,1), rather than the per-pixel
    // view vector used by a conventional perspective BRDF.  CB0[148]
    // and the reconstructed receiver normal are already consumed in
    // this same captured convention, so retain the literal +Z term.
    float3 halfVector = l + float3(0.0, 0.0, 1.0);
    float3 h = halfVector * rsqrt(max(dot(halfVector, halfVector), 0.000001));
    #if defined(ACTOR_HAIR_COVER)
    float unityShadow = 1.0;
    #else
    float unityShadow = SHADOW_ATTENUATION(input);
    #endif
    float capturedShadow = CapturedActorShadow(input.worldPosition, definition.r);
    float shadow = lerp(unityShadow, capturedShadow, saturate(_UseCapturedActorShadow));
    if (_FaceDebugMode > 11.5 && _FaceDebugMode < 12.5)
        return float4(shadow.xxx, 1.0);
    // Bound type-6 PS 0FFEAF8D uniquely multiplies the shadow blend
    // by CB0[145].x. That value is exactly zero, forcing its shadow
    // factor to one. This prevents the head from painting a black
    // polygon onto the neck/face-cap material.
    float materialShadow = isFaceCap
        ? lerp(1.0, shadow, saturate(_CapturedActorFacePartsShadowStrength))
        : shadow;

    // The type-8 branch first applies its UV-space 2048px HighlightMap
    // as a hard stepped, pre-diffuse highlight. Bound CB2[8]=(0.15,0)
    // collapses the compiled smoothstep to a step at N.H^4=0.15. The upper
    // UV corner is explicitly excluded by the original shader.
    float earlySmoothedShadow = materialShadow *
        (4.0 * materialShadow * materialShadow - 6.0 * materialShadow + 3.0);
    // Hair strands use their authored highlight, while accessories in the
    // upper UV corner retain the conventional specular response. Boundaries
    // belong to strands: both UV components must be strictly above 0.75.
    float hairAccessoryMask = (input.uv.x > 0.75 && input.uv.y > 0.75) ? 1.0 : 0.0;
    if (isHair)
    {
        // Authored hair highlights retain the camera-facing receiver basis
        // and fixed +Z half-vector term even when the ramp uses a world-space
        // light. Switching to a conventional world N.H moves the painted
        // highlight independently of the material's intended angular gate.
        float3 hairHalfVector = l + float3(0.0, 0.0, 1.0);
        float3 hairHalf = hairHalfVector * rsqrt(max(dot(hairHalfVector, hairHalfVector), 0.000001));
        float3 hairReceiver = lerp(n, capturedReceiverNormal, saturate(_UseCapturedReceiverNormal));
        float hairResponse = pow(saturate(dot(hairReceiver, hairHalf)), 4.0);
        float hairHighlightResponse = _SpecularThreshold.y <= 0.00001
            ? step(_SpecularThreshold.x, hairResponse)
            : smoothstep(_SpecularThreshold.x - _SpecularThreshold.y,
                _SpecularThreshold.x + _SpecularThreshold.y, hairResponse);
        float hairHighlightWeight = hairHighlightResponse *
            min(saturate(earlySmoothedShadow), saturate(definition.a)) *
            (1.0 - hairAccessoryMask);
        baseSample.rgb = lerp(
            baseSample.rgb,
            tex2Dbias(_HighlightTex, actorTextureCoordinate).rgb,
            hairHighlightWeight);
    }

    // The final authored lookup is RampAddMap (t7: 128x8 for fktn
    // hair), not HighlightMap. X = saturate(2*Def.r-1 + N.V),
    // Y = low-nibble(COLOR.g)/15 from VS 54E7B147; RGB*(1-A) is
    // added to both Base and Shade before
    // the captured Base/Shade/Ramp equation.
    float3 capturedSpecularModulation = 1.0.xxx;
    bool hasCapturedRampAddBranch = !isTypeOne ||
        _CapturedType1Variant < 1.5;
    if (!isEye && !isEyeHighlight && hasCapturedRampAddBranch)
    {
        float rampAddX = saturate(definition.r * 2.0 - 1.0 + saturate(dot(n, v)));
        float4 authoredRampAdd = tex2D(_RampAddTex, float2(rampAddX, saturate(vertexDefinition)));
        if (isTypeZeroDebug && _FaceDebugMode > 27.5 && _FaceDebugMode < 28.5)
            return float4(authoredRampAdd.aaa, 1.0);
        float3 rampAddColor = authoredRampAdd.rgb * _RampAddColor.rgb;
        float3 authoredRampAddRgb = rampAddColor * (1.0 - authoredRampAdd.a);
        if (isTypeZeroDebug && _FaceDebugMode > 17.5 && _FaceDebugMode < 18.5)
            return float4(authoredRampAddRgb, 1.0);
        baseSample.rgb += authoredRampAddRgb;
        shadeSample.rgb += authoredRampAddRgb;
        // Diffuse and specular use different alpha roles: only the
        // additive diffuse term is premultiplied by (1-A). Specular
        // interpolates toward the unpremultiplied tinted lookup.
        // Using the diffuse term here incorrectly zeros reflections
        // at A=1. The no-RampAdd variant retains a multiplier of 1.
        capturedSpecularModulation = lerp(
            1.0.xxx, rampAddColor, saturate(authoredRampAdd.a));
    }

    if (_FaceDebugMode > 8.5 && _FaceDebugMode < 9.5)
    {
        float3 typeColor = abs(_ShaderType - 0.0) < 0.25 ? float3(1, 0, 0) :
                           abs(_ShaderType - 1.0) < 0.25 ? float3(0, 1, 0) :
                           abs(_ShaderType - 3.0) < 0.25 ? float3(0, 0, 1) :
                           abs(_ShaderType - 4.0) < 0.25 ? float3(0, 1, 1) :
                           abs(_ShaderType - 5.0) < 0.25 ? float3(1, 0, 1) :
                           abs(_ShaderType - 6.0) < 0.25 ? float3(1, 1, 0) :
                           abs(_ShaderType - 8.0) < 0.25 ? float3(1, 0.35, 0) : float3(1, 1, 1);
        return float4(typeColor, 1.0);
    }

    float typeZeroDebug = 1.0 - saturate(abs(_ShaderType) * 4.0);
    if (typeZeroDebug > 0.5 && _FaceDebugMode > 6.5 && _FaceDebugMode < 8.5)
        return _FaceDebugMode < 7.5 ? float4(baseSample.rgb * _Color.rgb, 1.0) : float4(shadeSample.rgb, 1.0);

    // Command-line diagnostics used by the reconstruction harness. These
    // isolate authored inputs from lighting without changing production output.
    if (isSkin && _FaceDebugMode > 0.5 && _FaceDebugMode < 4.5)
    {
        if (_FaceDebugMode < 2.5) return float4(shadeSample.rgb, 1.0);
        if (_FaceDebugMode < 3.5) return float4(layer.rgb, 1.0);
        return float4(n * 0.5 + 0.5, 1.0);
    }

    if (isEyeHighlight)
    {
        // Bound type-5 PS 3F2C6EDC has no Shade/Ramp/environment
        // contribution in this material state: CB2[2]=(1,0,0,0)
        // makes metallic/spec visibility zero. It still applies the
        // common 0.96 direct diffuse and authored rim. m_ehl's HDR
        // Include the material tint once on this separate early-exit path;
        // it does not enter the common ramp/saturation/BRDF path below.
        baseSample.rgb *= _Color.rgb;
        float3 exactViewRimNormal = normalize(mul((float3x3)UNITY_MATRIX_V, n));
        float3 selectedRimNormal = normalize(lerp(
            n, exactViewRimNormal, saturate(_UseExactViewRimBasis)));
        float3 selectedRimDirection = normalize(lerp(
            _CapturedRimDirection.xyz, _CapturedRimViewDirection.xyz,
            saturate(_UseExactViewRimBasis)));
        float eyeHighlightRim = pow(
            1.0 - saturate(dot(selectedRimNormal, selectedRimDirection)),
            _CapturedRimParameters.z);
        float eyeHighlightRimMask = saturate(input.packedRimMask);
        float3 eyeHighlightRimColor = lerp(
            1.0.xxx, baseSample.rgb, _CapturedRimParameters.y) *
            _ActorRimColor.rgb;
        float3 eyeHighlightLit = baseSample.rgb * 0.96 *
            _ActorKeyColor.rgb * _CapturedLightColor.rgb * _ActorEyeHighlightColor.rgb;
        eyeHighlightLit += eyeHighlightRimColor * eyeHighlightRim * eyeHighlightRimMask;
        // The 0.97 Actor calibration was fitted to the common
        // surface variants and is not present in bound type-5 PS
        // 3F2C6EDC.  Keep this special additive branch literal;
        // _CapturedType5OutputScale exists only for paired A/B.
        return float4(max(
            eyeHighlightLit * _CapturedType5OutputScale,
            0.0), 1.0);
    }

    float ndl = dot(receiverNormal, l);
    float halfLambert = ndl * 0.5 + 0.5;
    float definitionShift = (definition.r - _DefValue.x) * (isFace ? 0.55 : 0.36);
    float shadowedLight = saturate(halfLambert * lerp(0.42, 1.0, materialShadow) + definitionShift);
    // The common surface branch uses the camera-facing receiver normal.
    if (isSkin) shadowedLight = saturate(halfLambert * lerp(0.38, 0.92, shadow) + definitionShift - 0.055);
    // Exact register probes of bound type-0 DXBC 5C07CB70 disproved the
    // earlier Def.r-only interpretation. r10.x is overwritten before the
    // material-type movc and every non-type-9 branch receives
    // saturate(0.5*N.L + 0.5 + Def.r - 0.65). The N.L is evaluated in
    // the camera-facing receiver basis (r6), because CB0[148].w is zero.
    // This drives the 1024x4 RampMap substantially farther toward its
    // bright/low-alpha end than sampling at raw Def.r.
    float exactReceiverHalfLambert = dot(lerp(capturedReceiverNormal, n,
        saturate(_CapturedLightDirection.w)), l) * 0.5 + 0.5;
    float exactSurfaceRamp = saturate(exactReceiverHalfLambert + definition.r -
        0.5 - 0.5 * _ActorMatcapParameters.x);
    // Paired with the offline exactrampcoord4 DXBC patch.  Retain
    // transparent Base alpha so registration and scalar values are
    // compared under the same premultiplied-source contract.
    if (isEye && _CapturedType4DebugStage > 2.5 &&
        _CapturedType4DebugStage < 3.5)
        return float4((exactSurfaceRamp * baseSample.a).xxx, baseSample.a);
    // VS 54E7B147 writes TEXCOORD4 as
    //   CB2[9].xyz*N.x + CB2[10].xyz*N.y + CB2[11].xyz*N.z.
    // The vectors form the animated reflection basis: negative head right,
    // head up and head forward. The publisher supplies the reflected X axis.
    // Type-9 PS 717B09C5 transforms that interpolant through the same
    // receiver basis, evaluates the same shifted half-Lambert surface,
    // takes max(common, head), then blends the extra response by Def.b.
    float3 headWorldNormal = normalize(
        _HeadRightDirection.xyz * input.objectNormal.x * faceSign +
        _HeadUpDirection.xyz * input.objectNormal.y * faceSign +
        _HeadDirection.xyz * input.objectNormal.z * faceSign);
    float3 headReceiverNormal = float3(
        dot(receiverBasisX, headWorldNormal),
        dot(receiverBasisY, headWorldNormal),
        dot(v, headWorldNormal));
    float headSurfaceRamp = saturate(
        dot(lerp(headReceiverNormal, headWorldNormal, saturate(_CapturedLightDirection.w)), l) *
        0.5 + definition.r - 0.5 * _ActorMatcapParameters.x);
    float type9Ramp = lerp(
        exactSurfaceRamp,
        max(exactSurfaceRamp, headSurfaceRamp),
        saturate(definition.b));
    float smoothedShadow = earlySmoothedShadow;
    float rampX = min(saturate(isSkin ? type9Ramp : exactSurfaceRamp), saturate(smoothedShadow));
    // Paired with exactrampx4, which preserves the transient type-4
    // `min(smoothedShadow, exactSurfaceRamp)` before the ramp sample.
    if (isEye && _CapturedType4DebugStage > 4.5 &&
        _CapturedType4DebugStage < 5.5)
        return float4((rampX * baseSample.a).xxx, baseSample.a);
    if (isTypeZeroDebug && _FaceDebugMode > 16.5 && _FaceDebugMode < 17.5)
        return float4(rampX.xxx, 1.0);
    // The captured Actor PS feeds its primary ramp (t7) a lighting value in X
    // and a literal zero in Y. Sampling the middle row changed the authored
    // face/body response on these very short lookup textures.
    // The Android-origin eye RampMap reaches the local D3D11
    // Texture2D with its U lookup reversed relative to captured PS
    // A155A892. Exact raw-ramp plus r10 authored-diffuse probes close
    // only under this type-4 correction; other Actor types retain
    // their already-verified coordinate contract.
    float type4RampX = lerp(rampX, 1.0 - rampX,
        isEye ? saturate(_CapturedType4RampFlip) : 0.0);
    float4 ramp = tex2D(_RampTex, float2(type4RampX, 0.0));
    if (isEye && _CapturedType4DebugStage > 8.5 &&
        _CapturedType4DebugStage < 9.5)
        return ramp;
    if (isTypeZeroDebug && _FaceDebugMode > 23.5 && _FaceDebugMode < 24.5)
        return float4(ramp.rgb, 1.0);
    if (isTypeZeroDebug && _FaceDebugMode > 24.5 && _FaceDebugMode < 25.5)
        return float4(ramp.aaa, 1.0);
    if (isTypeZeroDebug && _FaceDebugMode > 25.5 && _FaceDebugMode < 26.5)
        return float4(shadeSample.aaa, 1.0);
    if (isTypeZeroDebug && _FaceDebugMode > 26.5 && _FaceDebugMode < 27.5)
        return float4(baseSample.aaa, 1.0);
    if (_FaceDebugMode > 9.5 && _FaceDebugMode < 10.5)
        return float4(ramp.aaa, 1.0);
    if (_FaceDebugMode > 10.5 && _FaceDebugMode < 11.5)
        return float4(shadeSample.aaa, 1.0);

    float3 shadeTarget = isFace
        ? baseSample.rgb * shadeSample.rgb
        : shadeSample.rgb;
    float shadowFloor = isFace ? 0.70 : isHair ? 0.28 : 0.43;
    float shadeBlend = lerp(shadowFloor, 1.0, saturate(rampX + definition.g * 0.10 + (vertexDefinition - 0.5) * 0.05));
    if (isSkin) shadeBlend = max(shadeBlend, 0.95);
    float3 diffuse = lerp(shadeTarget, baseSample.rgb, shadeBlend);
    diffuse *= lerp(ramp.rgb, 1.0.xxx, 0.68 + shadeBlend * 0.32);

    // The variants actually bound in this frame are 717B09C5 (type 9),
    // 73721FD0 (type 8) and 023B1567 (type 3), rather than the unbound
    // 61473D superset used for the first reconstruction. Their instructions
    // 213-222 apply captured CB0[150].rgb to both ShadeMap and the ramp:
    //   A = lerp(Base, Shade * tint, Ramp.a)
    //   B = Base * Ramp.rgb * lerp(1, tint, Ramp.a)
    //   Diffuse = lerp(A, B, Shade.a)
    // CB0[149].z is exactly 1 in all seven actor draws.
    float3 capturedTint = _CapturedShadeTint.rgb;
    float shadeStrength = saturate(_ActorMatcapParameters.z);
    float3 capturedShade = lerp(baseSample.rgb, shadeSample.rgb * capturedTint, saturate(ramp.a) * shadeStrength);
    float3 capturedRampBase = baseSample.rgb * lerp(1.0.xxx,
        ramp.rgb * lerp(1.0.xxx, capturedTint, saturate(ramp.a)), shadeStrength);
    float3 capturedDiffuse = lerp(capturedShade, capturedRampBase, saturate(shadeSample.a));
    bool debugSelectedType1 = isTypeOne &&
        abs(_CapturedType1Variant - _CapturedType1DebugVariant) < 0.25;
    if (debugSelectedType1 && _CapturedType1DebugStage > 4.5 &&
        _CapturedType1DebugStage < 5.5)
        return float4(mul((float3x3)UNITY_MATRIX_V, n), 1.0);
    if (debugSelectedType1 && _CapturedType1DebugStage > 5.5 &&
        _CapturedType1DebugStage < 6.5)
        return float4(max(n, 0.0), 1.0);
    if (debugSelectedType1 && _CapturedType1DebugStage > 0.5 &&
        _CapturedType1DebugStage < 1.5)
        return float4(capturedDiffuse * _Color.rgb, 1.0);
    if (isEye && _CapturedType4DebugStage > 0.5 &&
        _CapturedType4DebugStage < 1.5)
        return float4(max(capturedDiffuse * _Color.rgb * baseSample.a, 0.0), baseSample.a);
    // The signed replay now patches r13 on both opaque ps9300 and
    // transparent ps9360 type-8 variants.  Expose the same authored
    // diffuse stage for every local Actor material so hair can be
    // compared before BRDF/rim/temporal energy is added.
    if (_FaceDebugMode > 15.5 && _FaceDebugMode < 16.5)
        return float4(capturedDiffuse * _Color.rgb, 1.0);
    diffuse = lerp(diffuse, capturedDiffuse, saturate(_CapturedDiffuseBlend));
    if (isSkin && _FaceDecalCount > 0.5)
        diffuse = ApplyOriginalFaceDecals(diffuse, input.worldPosition);
    // Shade alpha marks skin even inside mixed body/clothing materials.
    // The runtime profile stores a delta: zero preserves authored saturation.
    float skinSaturationDelta = _CapturedSkinSaturation * saturate(shadeSample.a);
    if (abs(skinSaturationDelta) > 0.00001)
    {
        // This stage is linear RGB; use linear-light luminance, not the
        // gamma-encoded luma weights used by the earlier face-only path.
        float skinLuma = dot(diffuse, float3(0.2126729, 0.7151522, 0.0721750));
        diffuse = lerp(skinLuma.xxx, diffuse,
            max(1.0 + skinSaturationDelta, 0.0));
    }
    // Apply once after every authored diffuse contribution and skin saturation,
    // before constructing BRDF and surface-tinted rim. This also handles zero
    // color components without dividing an already tinted sample.
    diffuse *= _Color.rgb;

    // Literal BRDF from bound 5C07/7372/717B DXBC.  The t1 resource
    // operand is explicitly swizzled xzyw: sampled r10.y is source
    // Def.b and r10.z is source Def.g.  Consequently r4.y/metallic
    // is Def.b, while r4.z/smoothness is Def.g.  Reading register
    // names without the resource swizzle produces a plausible but
    // wrong channel swap and removes most costume diffuse energy.
    float capturedSHValid = step(0.01, abs(_CapturedSH0.w) + abs(_CapturedSH1.w) + abs(_CapturedSH2.w));
    float3 ambient = lerp(max(ShadeSH9(float4(n, 1.0)), 0.0),
                          CapturedActorSH(n),
                          capturedSHValid);
    float metallic = isSkin ? 0.0 : saturate(definition.b);
    float smoothness = saturate(definition.g * _ActorMatcapParameters.y);
    float dielectricDiffuse = 0.96 * (1.0 - metallic);
    float3 directDiffuse = diffuse * dielectricDiffuse * _CapturedDirectScale;
    if (debugSelectedType1 && _CapturedType1DebugStage > 1.5 &&
        _CapturedType1DebugStage < 2.5)
        return float4(directDiffuse, 1.0);
    if (isEye && _CapturedType4DebugStage > 1.5 &&
        _CapturedType4DebugStage < 2.5)
        return float4(max(directDiffuse * baseSample.a, 0.0), baseSample.a);
    if (_FaceDebugMode > 18.5 && _FaceDebugMode < 19.5)
        return float4(directDiffuse, 1.0);

    float perceptualRoughness = max(1.0 - smoothness, 0.0);
    float roughnessSquared = max(perceptualRoughness * perceptualRoughness, 0.0078125);
    float roughnessFourth = roughnessSquared * roughnessSquared;
    float3 specularF0 = lerp(0.04.xxx, diffuse, metallic);
    float grazingTerm = saturate(smoothness + 1.0 - dielectricDiffuse);
    // A155A892's type-4 branch selects r10 (the complete authored
    // diffuse) directly into r14.xyzw instead of the common
    // dielectric/metallic F0 path, and keeps r13.y =
    // saturate(smoothness + 0.04) as its grazing value. Exact r1
    // environment-BRDF replay shows that the common branch loses
    // essentially the entire iris specular lobe.
    float useType4DiffuseF0 = isEye ? saturate(_CapturedType4DiffuseF0) : 0.0;
    specularF0 = lerp(specularF0, diffuse, useType4DiffuseF0);
    grazingTerm = lerp(grazingTerm, saturate(smoothness + 0.04),
                       useType4DiffuseF0);
    float nv = saturate(dot(n, v));
    float fresnel4 = pow(1.0 - nv, 4.0);
    float3 environmentBrdf = lerp(specularF0, grazingTerm.xxx, fresnel4) /
                             (roughnessFourth + 1.0);
    if (debugSelectedType1 && _CapturedType1DebugStage > 11.5 &&
        _CapturedType1DebugStage < 12.5)
        return float4(environmentBrdf, 1.0);
    if (isEye && _CapturedType4DebugStage > 12.5 &&
        _CapturedType4DebugStage < 13.5)
        return float4(max(environmentBrdf, 0.0), 1.0);
    float reflectionMip = perceptualRoughness * (1.7 - 0.7 * perceptualRoughness) * 6.0;
    float3 geometricNormal = normalize(input.worldNormal) * faceSign;
    float3 worldReflectionDirection = reflect(-v, geometricNormal);
    // Actor CB1[0..3] is the exact local-to-captured-world rigid
    // transform for this GPA frame.  Its rotation is -94.9698 deg
    // around Y, not the provisional -90 deg axis permutation.  The
    // signed reflection Procrustes fit independently recovered the
    // same matrix (max element delta 0.00146), so use the captured
    // CB1 rotation literally for world-space probes/cube lookup.
    // Direct/ramp lighting remains in the reconstructed receiver basis.
    float3 capturedActorViewDirection = float3(
        -0.0866342783 * v.x - 0.996240199 * v.z,
        v.y,
        0.996240199 * v.x - 0.0866342783 * v.z);
    float3 capturedActorGeometricNormal = float3(
        -0.0866342783 * geometricNormal.x - 0.996240199 * geometricNormal.z,
        geometricNormal.y,
        0.996240199 * geometricNormal.x - 0.0866342783 * geometricNormal.z);
    // Direction adapters belong to the archived cube payload. Ordinary Unity
    // studio/story cubes are world-oriented for every material, including
    // type1 and eyes; do not rotate those into an unrelated captured scene.
    bool capturedEnvironment = _UseCapturedEnvironmentBasis > 0.5;
    float3 reflectionDirection = isTypeOne && capturedEnvironment
        ? float3(
            -0.0866342783 * worldReflectionDirection.x -
                0.996240199 * worldReflectionDirection.z,
            worldReflectionDirection.y,
            0.996240199 * worldReflectionDirection.x -
                0.0866342783 * worldReflectionDirection.z)
        : worldReflectionDirection;
    if (debugSelectedType1 && _CapturedType1DebugStage > 15.5 &&
        _CapturedType1DebugStage < 16.5)
        return float4(reflectionDirection, 1.0);
    if (debugSelectedType1 && _CapturedType1DebugStage > 16.5 &&
        _CapturedType1DebugStage < 17.5)
        return float4(reflectionMip.xxx, 1.0);
    if (debugSelectedType1 && _CapturedType1DebugStage > 17.5 &&
        _CapturedType1DebugStage < 18.5)
        return float4(capturedActorViewDirection, 1.0);
    if (debugSelectedType1 && _CapturedType1DebugStage > 18.5 &&
        _CapturedType1DebugStage < 19.5)
        return float4(capturedActorGeometricNormal, 1.0);
    if (debugSelectedType1 && _CapturedType1DebugStage > 19.5 &&
        _CapturedType1DebugStage < 20.5)
        return float4(
            -0.0866342783 * rawViewDirection.x -
                0.996240199 * rawViewDirection.z,
            rawViewDirection.y,
            0.996240199 * rawViewDirection.x -
                0.0866342783 * rawViewDirection.z,
            1.0);
    float3 transformedEyeReflection = capturedEnvironment
        ? TransformCapturedEyeCubeDirection(reflectionDirection)
        : reflectionDirection;
    float3 eyeEnvironmentCube = texCUBElod(_ActorEyeEnvironmentCube,
        float4(transformedEyeReflection, reflectionMip)).rgb;
    float3 eyeEnvironmentArray = SampleCapturedEyeEnvironmentArray(
        transformedEyeReflection, reflectionMip);
    float3 transformedActorReflection = capturedEnvironment
        ? TransformCapturedCubeDirection(reflectionDirection, _CapturedActorCubeTransformMode)
        : reflectionDirection;
    float3 actorEnvironmentCube = texCUBElod(_ActorEnvironmentCube,
        float4(transformedActorReflection, reflectionMip)).rgb;
    float3 actorEnvironmentArray = SampleCapturedActorEnvironmentArray(
        reflectionDirection, reflectionMip);
    float useActorEnvironmentArray = max(
        saturate(_UseCapturedActorEnvironmentArray),
        isTypeOne ? saturate(_UseCapturedType1ActorEnvironmentArray) : 0.0);
    float3 environmentReflection = isEye
        ? lerp(eyeEnvironmentCube, eyeEnvironmentArray,
            saturate(_UseCapturedEyeEnvironmentArray))
        : lerp(actorEnvironmentCube, actorEnvironmentArray,
            useActorEnvironmentArray);
    environmentReflection *= _ActorEnvironmentIntensity *
        (isEye ? _CapturedEyeReflectionColor.rgb :
                 _CapturedReflectionColor.rgb);
    if (debugSelectedType1 && _CapturedType1DebugStage > 10.5 &&
        _CapturedType1DebugStage < 11.5)
        return float4(environmentReflection, 1.0);
    if (isEye && _CapturedType4DebugStage > 11.5 &&
        _CapturedType4DebugStage < 12.5)
        return float4(max(environmentReflection, 0.0), 1.0);

    float nh = saturate(dot(receiverNormal, h));
    float lhSquared = max(dot(l, h) * dot(l, h), 0.1);
    // DXBC r15.w = roughnessFourth - 1, then
    // D = roughnessFourth /
    //   ((N.H^2*r15.w+1.00001)^2 * L.H^2 * (4*a+2)).
    // The old local path accidentally substituted smoothness for
    // r15.w and used world N.L as the final visibility gate.  That
    // error is especially view-dependent under an orbit camera.
    float capturedDistributionTerm = lerp(
        smoothness, roughnessFourth - 1.0,
        saturate(_UseCapturedDirectSpecular));
    float directSpecular = roughnessFourth /
        (max(pow(nh * nh * capturedDistributionTerm + 1.00001, 2.0) *
             lhSquared * (4.0 * roughnessSquared + 2.0), 1e-6));
    float directSpecularNdl = lerp(
        saturate(dot(n, l)), saturate(dot(receiverNormal, l)),
        saturate(_UseCapturedDirectSpecular));
    directSpecular *= directSpecularNdl;
    if (debugSelectedType1 && _CapturedType1DebugStage > 12.5 &&
        _CapturedType1DebugStage < 13.5)
        return float4(directSpecular.xxx, 1.0);
    if (isEye && _CapturedType4DebugStage > 13.5 &&
        _CapturedType4DebugStage < 14.5)
        return float4(max(directSpecular.xxx, 0.0), 1.0);
    float definitionVisibility = saturate(definition.a);
    // Apply after the authored highlight so suppressing the extra BRDF lobe
    // does not erase that highlight. Share the mask with additional lights.
    if (isHair) definitionVisibility *= hairAccessoryMask;
    float specularVisibility = min(saturate(smoothedShadow), definitionVisibility);
    if (isEye)
        specularVisibility = lerp(
            specularVisibility, definitionVisibility,
            saturate(_CapturedType4DefinitionVisibility));
    if (isFaceCap) specularVisibility = saturate(definition.a);
    if (debugSelectedType1 && _CapturedType1DebugStage > 13.5 &&
        _CapturedType1DebugStage < 14.5)
        return float4(specularVisibility.xxx, 1.0);
    // exactdefinitiona4 and exactvisibility4 differ by only 0.28%
    // in mean and correlate at 0.973 against the same local Def.a
    // field. The captured shadow term is non-limiting here.
    if (isEye && _CapturedType4DebugStage > 3.5 &&
        _CapturedType4DebugStage < 4.5)
        return float4((specularVisibility * baseSample.a).xxx, baseSample.a);

    float3 capturedPremodSpecular =
        (environmentReflection + directSpecular.xxx) * environmentBrdf *
        specularVisibility;
    if (debugSelectedType1 && _CapturedType1DebugStage > 14.5 &&
        _CapturedType1DebugStage < 15.5)
        return float4(capturedPremodSpecular, 1.0);
    float3 capturedSpecular = capturedPremodSpecular * capturedSpecularModulation;
    // Optional painted reflection is added independently of BRDF/Fresnel,
    // then shares material visibility and RampAdd tint with other reflections.
    // Keep the disabled path unchanged, including its floating-point grouping.
    [branch] if (_UseReflection > 0.5)
        capturedSpecular += SphereReflection(n, v) * specularVisibility * capturedSpecularModulation;
    // Type 6 stores its closed-eye highlight in the small upper UV corner.
    // It adds twice the ramped color, independently of Definition.A/BRDF;
    // exact boundary coordinates and other regions retain ordinary shading.
    if (isFaceCap && input.uv.x > 0.96875 && input.uv.y > 0.96875)
        capturedSpecular += 2.0 * diffuse * capturedSpecularModulation;
    if (debugSelectedType1 && _CapturedType1DebugStage > 8.5 &&
        _CapturedType1DebugStage < 9.5)
        return float4(capturedSpecular, 1.0);
    if (isEye && _CapturedType4DebugStage > 14.5 &&
        _CapturedType4DebugStage < 15.5)
        return float4(max(capturedSpecular, 0.0), 1.0);
    if (_FaceDebugMode > 19.5 && _FaceDebugMode < 20.5)
        return float4(capturedSpecular, 1.0);
    float3 capturedBrdf = directDiffuse + capturedSpecular;
    if (debugSelectedType1 && _CapturedType1DebugStage > 9.5 &&
        _CapturedType1DebugStage < 10.5)
        return float4(capturedBrdf, 1.0);
    if (_FaceDebugMode > 20.5 && _FaceDebugMode < 21.5)
        return float4(capturedBrdf, 1.0);
    // Sky irradiance uses the material's diffuse response, not raw albedo.
    // Metals have no diffuse sky lobe; the eye branch retains its authored
    // nonmetallic response. Keep direct-only intensity out of this term.
    float3 ambientDiffuse = diffuse * (isEye ? 0.96 : dielectricDiffuse);
    float3 lit = ambientDiffuse * ambient * _ActorLightingScales.x + capturedBrdf *
        _ActorKeyColor.rgb * _CapturedLightColor.rgb;
    // Explicit lights remain valid in command-buffer passes, where Unity does
    // not bind per-renderer lighting constants. Range and spot cone are local.
    [loop]
    for (int lightIndex = 0; lightIndex < _ActorAdditionalLightCount; lightIndex++)
    {
        float3 delta = _ActorAdditionalPositions[lightIndex].xyz - input.worldPosition;
        float distanceSquared = max(dot(delta, delta), 0.0001);
        float3 direction = delta * rsqrt(distanceSquared);
        float rangeFade = saturate(1.0 - distanceSquared * _ActorAdditionalPositions[lightIndex].w);
        float attenuation = rangeFade * rangeFade / max(1.0, distanceSquared);
        float outer = _ActorAdditionalDirections[lightIndex].w;
        if (outer > -0.99)
        {
            float cone = dot(-direction, _ActorAdditionalDirections[lightIndex].xyz);
            attenuation *= smoothstep(outer, max(outer + 0.0001, _ActorAdditionalSpots[lightIndex].x), cone);
        }
        float3 additionalHalf = direction + v;
        float3 halfDirection = additionalHalf * rsqrt(max(dot(additionalHalf, additionalHalf), 0.000001));
        float normalHalf = saturate(dot(n, halfDirection));
        float lightHalf = max(0.1, pow(dot(direction, halfDirection), 2.0));
        float distribution = roughnessFourth / max(0.000001,
            pow(normalHalf * normalHalf * (roughnessFourth - 1.0) + 1.00001, 2.0) *
            lightHalf * (4.0 * roughnessSquared + 2.0));
        float3 additionalSpecular = distribution * environmentBrdf * definitionVisibility *
            capturedSpecularModulation * _ActorLightingScales.z;
        // Preserve the already ramped, key-colored material under local lights.
        // The stylized angular gate normally stays open even on the back side;
        // range and spot attenuation still bound the illuminated region.
        float angularCoordinate = (1.0 + saturate(dot(n, direction))) * 1.178097;
        float angularWeight = smoothstep(_ActorMatcapParameters.x - 0.000488,
            _ActorMatcapParameters.x + 0.001464, angularCoordinate);
        float radiance = saturate(angularWeight + shadeStrength) * attenuation;
        // Keep this product within the additional-light branch. Sharing its
        // intermediate with the main/sky sum changes the compiled main pass's
        // multiply-add rounding even when no additional light is active.
        float3 mainLighting = capturedBrdf * (_ActorKeyColor.rgb * _CapturedLightColor.rgb);
        lit += (mainLighting + additionalSpecular) * _ActorAdditionalColors[lightIndex].rgb *
            radiance * _ActorLightingScales.y;
    }
    if (debugSelectedType1 && _CapturedType1DebugStage > 2.5 &&
        _CapturedType1DebugStage < 3.5)
        return float4(max(lit, 0.0), 1.0);
    // Offline GPA modes exactlightonly4/exactrimonly4 retain the
    // original type-4 PS and isolate only its b0 light terms while
    // forcing replace blend.  These local probes expose the same
    // premultiplied source components before the global 0.97 Actor
    // calibration so a final-source residual can be assigned to
    // BRDF/light or authored rim without fitting the composite.
    if (isEye && _CapturedType4DebugStage > 9.5 &&
        _CapturedType4DebugStage < 10.5)
        return float4(max(capturedBrdf * _ActorKeyColor.rgb *
                          _CapturedLightColor.rgb * baseSample.a, 0.0),
                      baseSample.a);

    // CB0[152]/[153] bound to every active actor draw: directional
    // authored rim, exponent 32, color 0.7, diffuse blend 0.85.
    float3 exactViewRimNormal = normalize(mul((float3x3)UNITY_MATRIX_V, n));
    float3 selectedRimNormal = normalize(lerp(
        n, exactViewRimNormal, saturate(_UseExactViewRimBasis)));
    float3 selectedRimDirection = normalize(lerp(
        _CapturedRimDirection.xyz, _CapturedRimViewDirection.xyz,
        saturate(_UseExactViewRimBasis)));
    float rimDirectional = pow(
        1.0 - saturate(dot(selectedRimNormal, selectedRimDirection)),
        _CapturedRimParameters.z);
    float rimMask = min(definition.r * 2.0, 1.0) * saturate(input.packedRimMask);
    if (debugSelectedType1 && _CapturedType1DebugStage > 7.5 &&
        _CapturedType1DebugStage < 8.5)
        return float4((rimDirectional * rimMask).xxx, 1.0);
    if (debugSelectedType1 && _CapturedType1DebugStage > 6.5 &&
        _CapturedType1DebugStage < 7.5)
        return float4(rimMask.xxx, 1.0);
    float3 capturedRimColor = lerp(1.0.xxx, diffuse, _CapturedRimParameters.y) *
                              _ActorRimColor.rgb;
    float3 capturedRim = capturedRimColor * rimDirectional * rimMask;
    if (debugSelectedType1 && _CapturedType1DebugStage > 3.5 &&
        _CapturedType1DebugStage < 4.5)
        return float4(max(capturedRim, 0.0), 1.0);
    if (isEye && _CapturedType4DebugStage > 10.5 &&
        _CapturedType4DebugStage < 11.5)
        return float4(max(capturedRim * baseSample.a, 0.0), baseSample.a);
    if (_FaceDebugMode > 21.5 && _FaceDebugMode < 22.5)
        return float4(capturedRim, 1.0);
    lit += capturedRim;
    lit += ramp.a * _CapturedShadeAdditive.rgb;
    lit += tex2D(_EmissionMap, input.uv).rgb * _EmissionColor.rgb * _UseEmission;
    if (_FaceDebugMode > 22.5 && _FaceDebugMode < 23.5)
        return float4(max(lit, 0.0), 1.0);

    // The newly recovered bound type-0/type-6 variants use the same authored
    // Base/Shade/Ramp contract and do not contain the former pale-albedo skin
    // detector or a procedural under-chin lobe.  Those heuristics caused the
    // orange hands and polygonal brown neck seen in pass 52, so production now
    // leaves skin hue entirely to the captured material maps and final LUT.

    // Transparent type-8 PS 73721FD0 and type-4 PS A155A892
    // premultiply RGB by Base.a before writing o1. Opaque type-8
    // PS 5C07CB70 writes alpha 1 and leaves RGB unscaled, so key
    // this distinction from the copied material blend contract.
    bool premultipliedActor = isEye || (isHair &&
        abs(_SrcBlend - 1.0) < 0.25 && abs(_DstBlend - 10.0) < 0.25);
    // SrcAlpha materials also need texture * material opacity. Leave their
    // RGB unscaled: the blend unit applies alpha once (including additive
    // SrcAlpha/One). Opaque variants still write 1; eye/hair keep their
    // separate premultiplied contract below.
    bool straightAlphaActor = abs(_SrcBlend - 5.0) < 0.25;
    float outputAlpha = (premultipliedActor || straightAlphaActor) ? baseSample.a : 1.0;
    #if defined(ACTOR_HAIR_COVER)
    clip(isHair ? 1.0 : -1.0);
    // Hair Base.a is an authored view-fade mask, not opacity. Unmarked
    // strands stay opaque; marked bangs reveal the eye near the front and
    // regain coverage at oblique/elevated head-relative viewing angles.
    float2 viewAlignment = float2(dot(v, _HeadDirection.xyz), abs(dot(v, _HeadUpDirection.xyz)));
    float2 obliqueCoverage = saturate(float2(
        _HairFadeParameters.x - viewAlignment.x, viewAlignment.y - _HairFadeParameters.z) *
        _HairFadeParameters.yw);
    outputAlpha = (1.0 - saturate(rawBaseSample.a) * (1.0 - max(obliqueCoverage.x, obliqueCoverage.y))) * _Color.a;
    premultipliedActor = false;
    #endif
    if (premultipliedActor) lit *= outputAlpha;
    // Type 4 is One/OneMinusSrcAlpha premultiplied compositing.  A
    // paired suppression probe must zero both RGB and alpha so the
    // destination is not attenuated; the production value is 1.
    float type4ProbeScale = isEye ? _CapturedType4OutputScale : 1.0;
    float type1VariantProbeScale =
        _CapturedType1Variant < 0.5 ? 1.0 :
        _CapturedType1Variant < 1.5 ? _CapturedType1BodyOutputScale :
        _CapturedType1HairOutputScale;
    float type1ProbeScale = isTypeOne
        ? _CapturedType1OutputScale * type1VariantProbeScale
        : 1.0;
    return float4(
        max(lit * _ActorColor.rgb * _CapturedActorOutputScale * type4ProbeScale * type1ProbeScale, 0.0),
        outputAlpha * type4ProbeScale * type1ProbeScale);
}
#endif
