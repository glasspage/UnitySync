Shader "Hidden/Glasspage/UnitySync/SelectionDilate"
{
    Properties
    {
        _MainTex ("Mask", 2D) = "black" {}
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float4 _Direction;
            float _Radius;

            fixed4 frag(v2f_img input) : SV_Target
            {
                int radius = min(max((int)round(_Radius), 0), 128);
                float mask = 0.0;

                [loop]
                for (int sampleOffset = -radius; sampleOffset <= radius; sampleOffset++)
                {
                    float2 offset = _Direction.xy * _MainTex_TexelSize.xy * sampleOffset;
                    mask = max(mask, tex2D(_MainTex, input.uv + offset).r);
                }

                return fixed4(mask, mask, mask, mask);
            }
            ENDCG
        }
    }
}
