Shader "Hidden/Glasspage/UnitySync/SelectionComposite"
{
    Properties
    {
        _MainTex ("Outer Mask", 2D) = "black" {}
        _InnerTex ("Inner Mask", 2D) = "black" {}
        _OutlineColor ("Outline Color", Color) = (1, 1, 1, 1)
        _FlipY ("Flip Y", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Overlay" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _InnerTex;
            fixed4 _OutlineColor;
            float _FlipY;

            fixed4 frag(v2f_img input) : SV_Target
            {
                float2 uv = input.uv;
                if (_FlipY > 0.5)
                {
                    uv.y = 1.0 - uv.y;
                }

                float outerMask = tex2D(_MainTex, uv).r;
                float innerMask = tex2D(_InnerTex, uv).r;
                float ring = saturate(outerMask - innerMask);
                return fixed4(_OutlineColor.rgb, ring * _OutlineColor.a);
            }
            ENDCG
        }
    }
}
