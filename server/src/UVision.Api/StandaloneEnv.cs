namespace UVision.Api;

/// <summary>standalone dev 의 평면 환경변수(.env)를 UVision 섹션 키로 매핑. 임베드 호스트는 미사용.</summary>
internal static class StandaloneEnv
{
    public static Dictionary<string, string?> ToConfig()
    {
        var map = new Dictionary<string, string?>();
        void Put(string env, string key)
        {
            var v = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(v)) map[key] = v;
        }
        Put("VLM_PROVIDER", "UVision:Vlm:Provider");
        Put("VLM_MODEL", "UVision:Vlm:Model");
        Put("VLM_API_KEY", "UVision:Vlm:ApiKey");
        Put("VLM_ENDPOINT", "UVision:Vlm:Endpoint");
        Put("MAX_UPLOAD_SIZE_MB", "UVision:Vlm:MaxUploadSizeMb");
        // 전용 ML 분류기(③ 2중체크) — VLM_* 와 대칭인 평면 env. 기본 none(VLM 단독).
        Put("ML_PROVIDER", "UVision:Ml:Provider");
        Put("ML_ENDPOINT", "UVision:Ml:Endpoint");
        Put("ML_MODEL", "UVision:Ml:Model");
        Put("ML_TOKEN", "UVision:Ml:Token");
        Put("ML_IMAGE_COLUMN", "UVision:Ml:ImageColumn");
        Put("ML_REVIEW_CONFIDENCE_THRESHOLD", "UVision:Ml:ReviewConfidenceThreshold");
        Put("ADMIN_PIN", "UVision:AdminPin");
        // Storage__DataPath: .NET 시절부터 문서화된 기존 override 키(하위호환 보존). VLM_* 는 Python .env 유래.
        Put("Storage__DataPath", "UVision:Storage:DataPath");
        return map;
    }
}
