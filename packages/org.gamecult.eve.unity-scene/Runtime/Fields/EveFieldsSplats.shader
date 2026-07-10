Shader "Eve/Fields/Splats"
{
    Properties { _ValueScale("Value Scale", Float) = 1 }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }
        Pass
        {
            Name "Add"
            Cull Off ZWrite Off ZTest Always
            BlendOp Add
            Blend One One
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            #include "EveFieldsSplatsCore.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "Max"
            Cull Off ZWrite Off ZTest Always
            BlendOp Max
            Blend One One
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            #include "EveFieldsSplatsCore.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "Alpha"
            Cull Off ZWrite Off ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            #include "EveFieldsSplatsCore.hlsl"
            ENDHLSL
        }
    }
}
