Shader "Hidden/CHM/VertexDot"
{
    Properties
    {
        _Color ("Color", Color) = (0, 0.8, 1, 0.5)
        _Size ("Size", Float) = 0.003
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Name "ZTestOn"
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            #ifdef UNITY_INSTANCING_ENABLED
            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(float4, _WorldPos)
            UNITY_INSTANCING_BUFFER_END(Props)
            #endif

            float _Size;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);

                #ifdef UNITY_INSTANCING_ENABLED
                float4 worldPos = UNITY_ACCESS_INSTANCED_PROP(Props, _WorldPos);
                #else
                float4 worldPos = float4(0, 0, 0, 1);
                #endif

                // ビルボード: カメラに向くクアッド
                float3 camRight = UNITY_MATRIX_V[0].xyz;
                float3 camUp = UNITY_MATRIX_V[1].xyz;

                // カメラからの距離に応じてサイズ調整
                float dist = length(_WorldSpaceCameraPos - worldPos.xyz);
                float scale = _Size * dist;

                float3 worldVertex = worldPos.xyz
                    + camRight * v.vertex.x * scale
                    + camUp * v.vertex.y * scale;

                o.pos = UnityWorldToClipPos(float4(worldVertex, 1));
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);

                #ifdef UNITY_INSTANCING_ENABLED
                return UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                #else
                return fixed4(0, 0.8, 1, 0.5);
                #endif
            }
            ENDCG
        }

        Pass
        {
            Name "ZTestOff"
            ZTest Always

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            #ifdef UNITY_INSTANCING_ENABLED
            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(float4, _WorldPos)
            UNITY_INSTANCING_BUFFER_END(Props)
            #endif

            float _Size;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);

                #ifdef UNITY_INSTANCING_ENABLED
                float4 worldPos = UNITY_ACCESS_INSTANCED_PROP(Props, _WorldPos);
                #else
                float4 worldPos = float4(0, 0, 0, 1);
                #endif

                float3 camRight = UNITY_MATRIX_V[0].xyz;
                float3 camUp = UNITY_MATRIX_V[1].xyz;

                float dist = length(_WorldSpaceCameraPos - worldPos.xyz);
                float scale = _Size * dist;

                float3 worldVertex = worldPos.xyz
                    + camRight * v.vertex.x * scale
                    + camUp * v.vertex.y * scale;

                o.pos = UnityWorldToClipPos(float4(worldVertex, 1));
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);

                #ifdef UNITY_INSTANCING_ENABLED
                return UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                #else
                return fixed4(0, 0.8, 1, 0.3);
                #endif
            }
            ENDCG
        }
    }
}
