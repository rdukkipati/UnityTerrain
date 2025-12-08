Shader "Custom/LitVertexColor"
//Raghav Dukkipati
//Bare bones shader to allow me to use mesh.colors on my terrain
//provided by chatGPT
{
    Properties
    {
        _Glossiness ("Smoothness", Range(0,1)) = 0.05
        _Metallic ("Metallic", Range(0,1)) = 0.0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows

        struct Input
        {
            float4 color : COLOR;
        };

        float _Glossiness;
        float _Metallic;

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // Use the vertex color
            o.Albedo = IN.color.rgb;
            o.Alpha = 1.0;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
        }
        ENDCG
    }

    FallBack "Diffuse"
}