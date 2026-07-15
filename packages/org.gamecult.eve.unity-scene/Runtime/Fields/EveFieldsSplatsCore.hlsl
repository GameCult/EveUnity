#ifndef EVE_FIELDS_SPLATS_CORE_INCLUDED
#define EVE_FIELDS_SPLATS_CORE_INCLUDED

struct EveFieldsSplat
{
    float4 centerHalfExtent;
    float4 rotationChannelFalloff;
    float4 layerSource;
    float4 sourceFrequencyPhase;
    float4 falloffParameters;
    float4 value;
};

StructuredBuffer<EveFieldsSplat> _EveFieldsSplats;
float4x4 _EveFieldsViewportToClip;
int _EveFieldsSplatCount;
int _EveFieldsChannelFilter;
float _EveFieldsSimulationTimeSeconds;
float _ValueScale;

// Ashima Arts webgl-noise, translated by Keijiro Takahashi.
// Copyright (C) 2011 Ashima Arts. Distributed under the MIT License.
float3 EveFieldsMod289(float3 x)
{
    return x - floor(x / 289.0) * 289.0;
}

float4 EveFieldsMod289(float4 x)
{
    return x - floor(x / 289.0) * 289.0;
}

float4 EveFieldsPermute(float4 x)
{
    return EveFieldsMod289((x * 34.0 + 1.0) * x);
}

float4 EveFieldsTaylorInvSqrt(float4 r)
{
    return 1.79284291400159 - r * 0.85373472095314;
}

float EveFieldsSimplexNoise3D(float3 v)
{
    const float2 C = float2(1.0 / 6.0, 1.0 / 3.0);
    float3 i = floor(v + dot(v, C.yyy));
    float3 x0 = v - i + dot(i, C.xxx);
    float3 g = step(x0.yzx, x0.xyz);
    float3 l = 1.0 - g;
    float3 i1 = min(g.xyz, l.zxy);
    float3 i2 = max(g.xyz, l.zxy);
    float3 x1 = x0 - i1 + C.xxx;
    float3 x2 = x0 - i2 + C.yyy;
    float3 x3 = x0 - 0.5;
    i = EveFieldsMod289(i);
    float4 p = EveFieldsPermute(EveFieldsPermute(EveFieldsPermute(
        i.z + float4(0.0, i1.z, i2.z, 1.0))
        + i.y + float4(0.0, i1.y, i2.y, 1.0))
        + i.x + float4(0.0, i1.x, i2.x, 1.0));
    float4 j = p - 49.0 * floor(p / 49.0);
    float4 x_ = floor(j / 7.0);
    float4 y_ = floor(j - 7.0 * x_);
    float4 x = (x_ * 2.0 + 0.5) / 7.0 - 1.0;
    float4 y = (y_ * 2.0 + 0.5) / 7.0 - 1.0;
    float4 h = 1.0 - abs(x) - abs(y);
    float4 b0 = float4(x.xy, y.xy);
    float4 b1 = float4(x.zw, y.zw);
    float4 s0 = floor(b0) * 2.0 + 1.0;
    float4 s1 = floor(b1) * 2.0 + 1.0;
    float4 sh = -step(h, 0.0);
    float4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
    float4 a1 = b1.xzyw + s1.xzyw * sh.zzww;
    float3 g0 = float3(a0.xy, h.x);
    float3 g1 = float3(a0.zw, h.y);
    float3 g2 = float3(a1.xy, h.z);
    float3 g3 = float3(a1.zw, h.w);
    float4 norm = EveFieldsTaylorInvSqrt(float4(
        dot(g0, g0), dot(g1, g1), dot(g2, g2), dot(g3, g3)));
    g0 *= norm.x;
    g1 *= norm.y;
    g2 *= norm.z;
    g3 *= norm.w;
    float4 m = max(0.6 - float4(
        dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0);
    m *= m;
    m *= m;
    return 42.0 * dot(m, float4(
        dot(x0, g0), dot(x1, g1), dot(x2, g2), dot(x3, g3)));
}

float EveFieldsCellDistanceB(float2 p)
{
    p = frac(p) - 0.5;
    return (length(p) * 1.5 + 0.25) *
        max(abs(p.x) * 0.866 + p.y * 0.5, -p.y);
}

float EveFieldsAnimatedCellNoiseB(float2 p, float time)
{
    float2 o = sin(float2(1.93, 0.0) + time) * 0.166;
    float a = EveFieldsCellDistanceB(p + float2(o.x, 0));
    float b = EveFieldsCellDistanceB(p + float2(0, 0.5 + o.y));
    p = mul(p + 0.5, -float2x2(0.5, -0.866, 0.866, 0.5));
    float c = EveFieldsCellDistanceB(p + float2(o.x, 0));
    float d = EveFieldsCellDistanceB(p + float2(0, 0.5 + o.y));
    p = mul(p + 0.5, -float2x2(0.5, -0.866, 0.866, 0.5));
    float e = EveFieldsCellDistanceB(p + float2(o.x, 0));
    float f = EveFieldsCellDistanceB(p + float2(0, 0.5 + o.y));
    return min(min(min(a, b), min(c, d)), min(e, f)) * 2.0;
}

struct Varyings
{
    float4 position : SV_POSITION;
    float2 localUv : TEXCOORD0;
    float2 fieldWorld : TEXCOORD2;
    nointerpolation uint instanceId : TEXCOORD1;
};

Varyings Vert(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
{
    static const float2 corners[6] = { float2(-1,-1), float2(1,-1), float2(1,1), float2(-1,-1), float2(1,1), float2(-1,1) };
    EveFieldsSplat splat = _EveFieldsSplats[instanceId];
    float2 local = corners[vertexId];
    float2 scaled = local * splat.centerHalfExtent.zw;
    float c = splat.rotationChannelFalloff.x;
    float s = splat.rotationChannelFalloff.y;
    float2 world = splat.centerHalfExtent.xy + float2(scaled.x*c-scaled.y*s, scaled.x*s+scaled.y*c);
    Varyings output;
    output.position = mul(_EveFieldsViewportToClip, float4(world, 0, 1));
    output.localUv = local;
    output.fieldWorld = world;
    output.instanceId = instanceId;
    return output;
}

float ResolveFalloff(float2 localUv, int falloff, float scale, float exponent)
{
    float distance01 = saturate(length(localUv));
    if (falloff == 0) return 1;
    if (falloff == 1) return saturate(1 - distance01);
    if (falloff == 3) return smoothstep(0, 1, distance01);
    if (falloff == 4)
    {
        float pulseDistance = distance01 * max(0, scale);
        return pow(saturate(1 - pulseDistance * pulseDistance), max(0.0001, exponent));
    }
    return 1 - smoothstep(0, 1, distance01);
}

float4 Frag(Varyings input) : SV_Target
{
    EveFieldsSplat splat = _EveFieldsSplats[input.instanceId];
    int channel = (int)round(splat.rotationChannelFalloff.z);
    if (_EveFieldsChannelFilter >= 0 && channel != _EveFieldsChannelFilter) discard;
    float alpha = ResolveFalloff(
        input.localUv,
        (int)round(splat.rotationChannelFalloff.w),
        splat.falloffParameters.x,
        splat.falloffParameters.y);
    clip(alpha - 0.0001);
    int sourceKind = (int)round(splat.layerSource.y);
    float source = 1;
    if (sourceKind == 1 || sourceKind == 2)
    {
        float timeCoordinate = sourceKind == 2
            ? _EveFieldsSimulationTimeSeconds * splat.layerSource.z
            : 0;
        source = EveFieldsSimplexNoise3D(float3(
            input.fieldWorld * splat.sourceFrequencyPhase.xy + splat.sourceFrequencyPhase.zw,
            timeCoordinate));
    }
    else if (sourceKind == 3)
    {
        source = EveFieldsAnimatedCellNoiseB(
            input.fieldWorld * splat.sourceFrequencyPhase.xy + splat.sourceFrequencyPhase.zw,
            _EveFieldsSimulationTimeSeconds * splat.layerSource.z);
    }
    else if (sourceKind == 4)
    {
        float distance01 = length(input.localUv);
        float radialExponent = max(0.0001, splat.sourceFrequencyPhase.y);
        source = cos(
            pow(distance01, radialExponent) * splat.sourceFrequencyPhase.x +
            splat.sourceFrequencyPhase.z +
            _EveFieldsSimulationTimeSeconds * splat.layerSource.z);
    }
    if (((int)round(splat.layerSource.w) & 1) != 0) source = abs(source);
    return splat.value * (alpha * source * _ValueScale);
}
#endif
