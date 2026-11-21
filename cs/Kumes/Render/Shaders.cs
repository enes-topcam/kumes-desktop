// GLSL kaynakları. Işık hesabı three.js MeshStandardMaterial'ın (fiziksel ışıklar) sadeleştirilmiş hali:
// Lambert dağınık + GGX parlama, yarımküre ışığı (AO ile), ACES Filmic ton eşleme, sRGB çıkış.
namespace Kumes.Render;

static class Shaders
{
	public const string PetVertex = """
		#version 330 core
		layout(location = 0) in vec3 aPos;
		layout(location = 1) in vec3 aNormal;
		layout(location = 2) in vec4 aTangent;
		layout(location = 3) in vec2 aUV;
		layout(location = 4) in uvec4 aJoints;
		layout(location = 5) in vec4 aWeights;

		uniform mat4 uBones[JOINT_COUNT];
		uniform mat4 uViewProj;

		out vec3 vNormal;
		out vec4 vTangent;
		out vec2 vUV;

		void main() {
			mat4 skin = aWeights.x * uBones[aJoints.x] + aWeights.y * uBones[aJoints.y]
			          + aWeights.z * uBones[aJoints.z] + aWeights.w * uBones[aJoints.w];
			vec4 world = skin * vec4(aPos, 1.0);
			mat3 m = mat3(skin);
			vNormal = m * aNormal;
			vTangent = vec4(m * aTangent.xyz, aTangent.w);
			vUV = aUV;
			gl_Position = uViewProj * world;
		}
		""";

	public const string PetFragment = """
		#version 330 core
		in vec3 vNormal;
		in vec4 vTangent;
		in vec2 vUV;

		uniform sampler2D uColorTex, uNormalTex, uAoTex, uAlphaTex;
		uniform int uAlphaTest; // 1: civcivin tüy katmanı (saydamlık maskesi, oyunda eşik 0.2)
		uniform vec3 uTint;
		uniform float uExposure;
		uniform vec3 uSky, uGround;
		uniform vec3 uSunDir, uSunColor, uRimDir, uRimColor;

		out vec4 fragColor;

		const float RECIPROCAL_PI = 0.3183098861837907;
		const float ROUGHNESS = 0.82;

		float pow2(float x) { return x * x; }

		vec3 F_Schlick(vec3 f0, float f90, float dotVH) {
			float fresnel = exp2((-5.55473 * dotVH - 6.98316) * dotVH);
			return f0 * (1.0 - fresnel) + f90 * fresnel;
		}
		float V_GGX_SmithCorrelated(float alpha, float dotNL, float dotNV) {
			float a2 = pow2(alpha);
			float gv = dotNL * sqrt(a2 + (1.0 - a2) * pow2(dotNV));
			float gl = dotNV * sqrt(a2 + (1.0 - a2) * pow2(dotNL));
			return 0.5 / max(gv + gl, 1e-6);
		}
		float D_GGX(float alpha, float dotNH) {
			float a2 = pow2(alpha);
			float denom = pow2(dotNH) * (a2 - 1.0) + 1.0;
			return RECIPROCAL_PI * a2 / pow2(denom);
		}
		vec3 BRDF_GGX(vec3 L, vec3 V, vec3 N, vec3 f0, float roughness) {
			float alpha = pow2(roughness);
			vec3 H = normalize(L + V);
			float dotNL = clamp(dot(N, L), 0.0, 1.0);
			float dotNV = clamp(dot(N, V), 0.0, 1.0);
			float dotNH = clamp(dot(N, H), 0.0, 1.0);
			float dotVH = clamp(dot(V, H), 0.0, 1.0);
			return F_Schlick(f0, 1.0, dotVH) * (V_GGX_SmithCorrelated(alpha, dotNL, dotNV) * D_GGX(alpha, dotNH));
		}

		vec3 RRTAndODTFit(vec3 v) {
			vec3 a = v * (v + 0.0245786) - 0.000090537;
			vec3 b = v * (0.983729 * v + 0.4329510) + 0.238081;
			return a / b;
		}
		vec3 ACESFilmic(vec3 color) {
			const mat3 ACESInputMat = mat3(
				vec3(0.59719, 0.07600, 0.02840),
				vec3(0.35458, 0.90834, 0.13383),
				vec3(0.04823, 0.01566, 0.83777));
			const mat3 ACESOutputMat = mat3(
				vec3( 1.60475, -0.10208, -0.00327),
				vec3(-0.53108,  1.10813, -0.07276),
				vec3(-0.07367, -0.00605,  1.07602));
			color *= uExposure / 0.6;
			color = ACESInputMat * color;
			color = RRTAndODTFit(color);
			color = ACESOutputMat * color;
			return clamp(color, 0.0, 1.0);
		}
		vec3 linearToSRGB(vec3 c) {
			return mix(c * 12.92, pow(c, vec3(1.0 / 2.4)) * 1.055 - 0.055, step(vec3(0.0031308), c));
		}

		vec3 directLight(vec3 L, vec3 color, vec3 N, vec3 V, vec3 albedo) {
			float dotNL = clamp(dot(N, L), 0.0, 1.0);
			vec3 irradiance = dotNL * color;
			return irradiance * (RECIPROCAL_PI * albedo + BRDF_GGX(L, V, N, vec3(0.04), ROUGHNESS));
		}

		void main() {
			vec3 albedo = texture(uColorTex, vUV).rgb * uTint;
			vec3 N = normalize(vNormal);
			vec3 T = normalize(vTangent.xyz);
			vec3 B = normalize(cross(N, T) * vTangent.w);
			vec3 mapN = texture(uNormalTex, vUV).xyz * 2.0 - 1.0;
			N = normalize(mat3(T, B, N) * mapN);
			float ao = texture(uAoTex, vUV).r;
			vec3 V = vec3(0.0, 0.0, 1.0); // dik kamera -Z'ye bakıyor

			vec3 c = directLight(uSunDir, uSunColor, N, V, albedo)
			       + directLight(uRimDir, uRimColor, N, V, albedo);
			float hemiW = 0.5 * N.y + 0.5;
			c += mix(uGround, uSky, hemiW) * RECIPROCAL_PI * albedo * ao;

			float alpha = 1.0;
			if (uAlphaTest == 1) {
				float a = texture(uAlphaTex, vUV).r;
				alpha = smoothstep(0.12, 0.40, a); // eşik ~0.2, yumuşak kenar (alpha-to-coverage)
				if (alpha <= 0.01) discard;
			}
			fragColor = vec4(linearToSRGB(ACESFilmic(c)), alpha);
		}
		""";

	// Tek üçgenle tüm ekranı kaplar (köşe verisi gerekmez)
	public const string FullscreenVertex = """
		#version 330 core
		void main() {
			vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
			gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
		}
		""";

	// Yumuşak kenarlı elips gölge; ön-çarpılmış alfa çıkışı
	public const string ShadowFragment = """
		#version 330 core
		uniform vec4 uShadow; // merkez x, merkez y (px, alttan), yarıçap x, yarıçap y
		uniform float uAlpha;
		out vec4 fragColor;
		void main() {
			vec2 d = (gl_FragCoord.xy - uShadow.xy) / uShadow.zw;
			float edge = 1.0 - length(d);
			float a = uAlpha * clamp(edge * uShadow.w, 0.0, 1.0);
			fragColor = vec4(0.0, 0.0, 0.0, a);
		}
		""";
}
