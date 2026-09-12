Shader "Hidden/Glasspage/UnitySync/SelectionComposite"
{
    Properties
    {
        _MainTex ("Outer Mask", 2D) = "black" {}
        _InnerTex ("Inner Mask", 2D) = "black" {}
        _OutlineColor ("Outline Color", Color) = (1, 1, 1, 1)
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

            fixed4 frag(v2f_img input) : SV_Target
            {
                float outerMask = tex2D(_MainTex, input.uv).r;
                float innerMask = tex2D(_InnerTex, input.uv).r;
                float ring = saturate(outerMask - innerMask);
                return fixed4(_OutlineColor.rgb, ring * _OutlineColor.a);
            }
            ENDCG
        }
    }
}
