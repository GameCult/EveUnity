Shader "Hidden/EveUnity/AdaptiveExposureMultiply"
{
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            Blend DstColor Zero
            ColorMask RGB

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            Texture2D<float> _ExposureTexture;

            struct Varyings
            {
                float4 position : SV_POSITION;
            };

            Varyings Vert(uint vertexId : SV_VertexID)
            {
                float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
                Varyings output;
                output.position = float4(uv * 2.0 - 1.0, 0.0, 1.0);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float exposure = _ExposureTexture.Load(int3(0, 0, 0));
                return exposure.xxxx;
            }
            ENDHLSL
        }
    }
}
