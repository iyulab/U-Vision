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
        Put("ML_REVIEW_CONFIDENCE_THRESHOLD", "UVision:Ml:ReviewConfidenceThreshold");
        // 확정 사례 기억 단(system 1) — 기본 none(단 없음). 임베딩은 mloop serve 의 /embed.
        Put("MEMORY_PROVIDER", "UVision:Memory:Provider");
        Put("MEMORY_ENDPOINT", "UVision:Memory:Endpoint");
        Put("MEMORY_ENCODER", "UVision:Memory:Encoder");
        Put("MEMORY_TOKEN", "UVision:Memory:Token");
        Put("ADMIN_PIN", "UVision:AdminPin");
        // Storage__DataPath: .NET 시절부터 문서화된 기존 override 키(하위호환 보존). VLM_* 는 Python .env 유래.
        Put("Storage__DataPath", "UVision:Storage:DataPath");
        return map;
    }
}
