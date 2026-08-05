using System.Security.Cryptography;
using System.Text;
using UVision.Api.Storage;

namespace UVision.Api.Auth;

/// <summary>최초 설정(<c>POST /admin/setup</c>) 결과 — 호출 엔드포인트가 HTTP 상태로 매핑한다.</summary>
public enum AdminSetupOutcome
{
    /// <summary>성공 — PIN 해시 기록·토큰 삭제 완료.</summary>
    Ok,
    /// <summary>명시적 config PIN 설정됨 → 부트스트랩 비활성(410).</summary>
    BootstrapDisabled,
    /// <summary>이미 PIN 이 설정됨(admin-pin.json 존재) → 재설정 불가(409).</summary>
    AlreadyConfigured,
    /// <summary>진행 중인 최초 설정 없음(토큰 파일 부재) → 서버 재시작 필요(409).</summary>
    NoPendingSetup,
    /// <summary>setup-token 불일치(401).</summary>
    InvalidToken,
    /// <summary>새 PIN 형식 위반(400).</summary>
    InvalidPin,
}

/// <summary>관리자 PIN 상태(클라이언트가 최초 설정 화면 노출 여부를 결정).</summary>
public sealed record AdminPinStatus(bool PinConfigured, bool SetupRequired);

/// <summary>
/// 관리자 PIN 의 단일 진실원 — 검증·상태·최초 설정(부트스트랩)을 관장한다.
///
/// <para><b>우선순위</b>: 명시적 config PIN(<c>UVision:AdminPin</c>/<c>ADMIN_PIN</c>) &gt;
/// 파일 해시(<c>{DataPath}/admin-pin.json</c>). config PIN 이 있으면 부트스트랩은 완전 비활성
/// (토큰 미생성·setup 410·파일 미조회) — 기존 동작과 byte-identical 한 opt-in additive 변경.</para>
///
/// <para><b>부트스트랩</b>: config·파일 어디에도 PIN 이 없으면 부팅 시 일회용 setup-token 을
/// <c>{DataPath}/admin-setup-token.txt</c> 에 생성한다(파일시스템 접근 = 신뢰 루트, 네트워크상
/// 선점 탈취 방지). 관리 콘솔이 토큰과 새 PIN 을 제출하면 PBKDF2 해시를 원자적 기록하고 토큰을 삭제한다.</para>
///
/// <para>read→write 원자성은 <see cref="SemaphoreSlim"/> 직렬화(단일 호스트 단일 프로세스 전제 —
/// <see cref="Storage.FileAuthorityStore"/> 와 동일). 세터업 성공 시 in-memory 해시를 즉시 갱신하므로
/// <b>재시작 없이</b> 곧바로 관리 기능을 쓸 수 있다.</para>
/// </summary>
public sealed class AdminPinService
{
    private readonly AdminPinOptions _options;
    private readonly StoragePaths _paths;
    private readonly ILogger<AdminPinService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private AdminPinHash? _fileHash;
    private bool _fileLoaded;

    public AdminPinService(AdminPinOptions options, StoragePaths paths, ILogger<AdminPinService> logger)
    {
        _options = options;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>명시적 config PIN 이 설정됨 → 부트스트랩 비활성(기존 동작 보존).</summary>
    public bool HasConfigPin => !string.IsNullOrEmpty(_options.Pin);

    /// <summary>제공 PIN 이 유효한지 검증한다(config 평문 또는 파일 해시).</summary>
    public async ValueTask<bool> VerifyAsync(string providedPin, CancellationToken ct = default)
    {
        if (HasConfigPin)
            return FixedTimeEquals(providedPin, _options.Pin!);

        var hash = await LoadFileHashAsync(ct);
        return hash is not null && AdminPinHashing.Verify(providedPin, hash);
    }

    /// <summary>PIN 이 어떤 경로로든 설정되었는지(미설정이면 관리 엔드포인트 503).</summary>
    public async ValueTask<bool> IsConfiguredAsync(CancellationToken ct = default) =>
        HasConfigPin || await LoadFileHashAsync(ct) is not null;

    /// <summary>클라이언트가 최초 설정 화면 노출 여부를 결정하기 위한 상태.</summary>
    public async ValueTask<AdminPinStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var configured = await IsConfiguredAsync(ct);
        // 최초 설정 필요 = 부트스트랩 활성(config PIN 없음) && 아직 파일 PIN 없음.
        var setupRequired = !HasConfigPin && !configured;
        return new AdminPinStatus(configured, setupRequired);
    }

    /// <summary>
    /// 최초 설정: setup-token 검증 후 새 PIN 해시를 원자적 기록하고 토큰을 삭제한다.
    /// 성공 시 in-memory 해시를 갱신해 재시작 없이 즉시 인증 가능.
    /// </summary>
    public async Task<AdminSetupOutcome> CompleteSetupAsync(
        string setupToken, string newPin, CancellationToken ct = default)
    {
        if (HasConfigPin) return AdminSetupOutcome.BootstrapDisabled;

        await _lock.WaitAsync(ct);
        try
        {
            // lock 하에서 디스크 재확인(다른 요청이 방금 설정했을 수 있음).
            var existing = await ReadFileHashFromDiskAsync(ct);
            if (existing is not null)
            {
                _fileHash = existing;
                _fileLoaded = true;
                return AdminSetupOutcome.AlreadyConfigured;
            }

            var tokenPath = _paths.AdminSetupTokenFile();
            if (!File.Exists(tokenPath))
                return AdminSetupOutcome.NoPendingSetup;

            var expectedToken = (await File.ReadAllTextAsync(tokenPath, ct)).Trim();
            if (string.IsNullOrEmpty(expectedToken) || !FixedTimeEquals(setupToken.Trim(), expectedToken))
                return AdminSetupOutcome.InvalidToken;

            if (!IsValidPin(newPin))
                return AdminSetupOutcome.InvalidPin;

            var hash = AdminPinHashing.Create(newPin);
            await StoragePaths.AtomicWriteJsonAsync(_paths.AdminPinFile(), hash, ct);
            _fileHash = hash;
            _fileLoaded = true;

            TryDeleteFile(tokenPath);
            _logger.LogInformation("관리자 PIN 최초 설정 완료 — setup-token 폐기, {Path} 기록.",
                _paths.AdminPinFile());
            return AdminSetupOutcome.Ok;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 부팅 시 부트스트랩 준비 — PIN 이 어디에도 없으면 일회용 setup-token 을 생성(이미 있으면 유지)하고
    /// 경로를 로그에 남긴다. config PIN 설정 또는 파일 PIN 존재 시 잔여 토큰을 청소한다.
    /// </summary>
    public async Task EnsureBootstrapAsync(CancellationToken ct = default)
    {
        var tokenPath = _paths.AdminSetupTokenFile();

        if (HasConfigPin)
        {
            TryDeleteFile(tokenPath); // config PIN 우선 — 잔여 토큰 제거(부트스트랩 비활성).
            return;
        }

        await _lock.WaitAsync(ct);
        try
        {
            var existing = await ReadFileHashFromDiskAsync(ct);
            if (existing is not null)
            {
                _fileHash = existing;
                _fileLoaded = true;
                TryDeleteFile(tokenPath); // 이미 설정됨 — 잔여 토큰 제거.
                return;
            }

            // 부트스트랩 대기 — 토큰 보장(재시작 간 idempotent).
            string token;
            if (File.Exists(tokenPath))
            {
                token = (await File.ReadAllTextAsync(tokenPath, ct)).Trim();
                if (string.IsNullOrEmpty(token))
                {
                    token = GenerateToken();
                    await File.WriteAllTextAsync(tokenPath, token, ct);
                }
            }
            else
            {
                token = GenerateToken();
                await File.WriteAllTextAsync(tokenPath, token, ct);
            }

            _logger.LogWarning(
                "관리자 PIN 미설정 — 최초 설정 대기. 관리 콘솔에서 아래 파일 내용을 setup-token 으로 입력하고 새 PIN 을 지정하세요: {Path}",
                tokenPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    // --- 내부 -------------------------------------------------------------

    /// <summary>파일 해시를 최초 1회 로드해 캐시(이후 setup 이 lock 하에 갱신).</summary>
    private async ValueTask<AdminPinHash?> LoadFileHashAsync(CancellationToken ct)
    {
        if (_fileLoaded) return _fileHash;

        await _lock.WaitAsync(ct);
        try
        {
            if (!_fileLoaded)
            {
                _fileHash = await ReadFileHashFromDiskAsync(ct);
                _fileLoaded = true;
            }
            return _fileHash;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async ValueTask<AdminPinHash?> ReadFileHashFromDiskAsync(CancellationToken ct)
    {
        var path = _paths.AdminPinFile();
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await System.Text.Json.JsonSerializer.DeserializeAsync<AdminPinHash>(
                stream, StoragePaths.Json, ct);
        }
        catch (System.Text.Json.JsonException)
        {
            _logger.LogError("손상된 관리자 PIN 파일 무시: {Path}", path);
            return null;
        }
    }

    /// <summary>URL-safe base64(padding 제거) — 복사·붙여넣기 가능한 일회용 토큰.</summary>
    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>PIN 최소 요건 — 공백 불가, 4자리 이상.</summary>
    private static bool IsValidPin(string pin) =>
        !string.IsNullOrWhiteSpace(pin) && pin.Trim().Length >= 4;

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best-effort 청소 — 실패해도 보안 불변식 무영향 */ }
    }
}
