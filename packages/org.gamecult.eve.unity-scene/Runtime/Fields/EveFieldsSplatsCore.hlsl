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
float _ValueScale;

float hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float valueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float a = hash21(i);
    float b = hash21(i + float2(1, 0));
    float c = hash21(i + float2(0, 1));
    float d = hash21(i + float2(1, 1));
    float2 u = f * f * (3 - 2 * f);
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y) * 2 - 1;
}

struct Varyings
{
    float4 position : SV_POSITION;
    float2 localUv : TEXCOORD0;
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
        float timeOffset = sourceKind == 2 ? _Time.y * splat.layerSource.z : 0;
        source = valueNoise(input.localUv * splat.sourceFrequencyPhase.xy + splat.sourceFrequencyPhase.zw + timeOffset);
        if (splat.layerSource.w != 0) source = abs(source);
    }
    return splat.value * (alpha * source * _ValueScale);
}
#endif
