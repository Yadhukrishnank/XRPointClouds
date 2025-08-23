Shader "PointCloud/BillboardURP"
{
    Properties
    {
        _ColorTex("Color", 2D) = "white" {}
        _Width("Width", Int) = 1
        _Height("Height", Int) = 1
        _PointSizeWorld("Point Size (World)", Float) = 0.01
        _AlphaCutoff("Alpha Cutoff", Range(0,1)) = 0.0
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="AlphaTest" "RenderType"="Opaque" }
        Cull Off
        ZWrite On
        AlphaToMask On

        Pass
        {
            Name "Forward"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Per-instance transforms written by compute
            StructuredBuffer<float4x4> matrixBuffer;

            TEXTURE2D(_ColorTex);
            SAMPLER(sampler_ColorTex);

            CBUFFER_START(UnityPerMaterial)
                int   _Width;
                int   _Height;
                float _PointSizeWorld;
                float _AlphaCutoff;
                float3 _CamRight;   // set from C# each frame
                float3 _CamUp;      // set from C# each frame
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;   // quad corners in [-0.5..0.5]
                uint   instanceID  : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 quadLocal  : TEXCOORD0;
                uint   id         : TEXCOORD1;
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;

                uint id = IN.instanceID;

                // Translation from instance matrix
                float4x4 M = matrixBuffer[id];
                float3 centerWS = float3(M[0][3], M[1][3], M[2][3]);

                // Expand a unit quad to a camera-facing billboard in world space
                float2 halfSize = IN.positionOS.xy * _PointSizeWorld;   // [-0.5..0.5] * size
                float3 worldPos = centerWS + _CamRight * halfSize.x + _CamUp * halfSize.y;

                OUT.positionCS = TransformWorldToHClip(worldPos);
                OUT.quadLocal  = IN.positionOS.xy;
                OUT.id         = id;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                // Circular alpha clip (turn quad into round disc)
                float r = length(IN.quadLocal * 2.0); // [-1..1] radius
                clip(1.0 - r - _AlphaCutoff);

                // Sample RGB by instance index (matches compute layout)
                uint w = (uint)_Width;
                uint u = IN.id % w;
                uint v = IN.id / w;
                float2 uv = float2((u + 0.5) / (float)_Width, (v + 0.5) / (float)_Height);

                half3 col = SAMPLE_TEXTURE2D(_ColorTex, sampler_ColorTex, uv).rgb;
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
}
