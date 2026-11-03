using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Opc.Ua;
using Opc.Ua.Server.Hosting;
using UVision.Api.Configuration;
using UVision.Api.Storage;

namespace UVision.Api;

/// <summary>
/// OPC UA 서버(P4 사건 전송) — 엔진 호스트에만 있다. 게시 패키지(<c>UVision.Hosting</c>)는 서버 스택을 싣지 않는다.
/// 기본 꺼짐: 설정이 없으면 아무것도 등록하지 않아 포트도 인증서 폴더도 생기지 않는다.
/// </summary>
public static class OpcUaServiceExtensions
{
    /// <summary>PKI 폴더(상대) — 데이터 루트 아래. 서버 인스턴스 인증서는 <c>own</c>, 클라이언트 신뢰는 <c>trusted</c>.</summary>
    public const string PkiFolder = "opcua/pki";

    public static IServiceCollection AddUVisionOpcUa(
        this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        var options = configuration.GetSection(UVisionOptions.SectionName).Get<UVisionOptions>() ?? new UVisionOptions();
        var opc = options.Events.OpcUa;
        Validate(opc);
        if (!opc.Enabled)
            return services;

        var pkiRoot = Path.Combine(new StoragePaths(options.Storage, contentRoot).Root, PkiFolder);
        var host = string.IsNullOrWhiteSpace(opc.Host) ? Dns.GetHostName() : opc.Host.Trim();

        services
            .AddOpcUa()
            .AddServer(o =>
            {
                o.ApplicationName = "U-Vision";
                o.ApplicationUri = $"urn:{host}:U-Vision";
                o.ProductUri = "uri:u-vision:engine";
                o.SubjectName = $"CN=U-Vision, DC={host}";
                // 클라이언트 인증서는 관리자가 trusted 폴더로 옮겨야 접속된다 — 자동 수락은 끈다.
                o.AutoAcceptUntrustedCertificates = false;
                o.PkiRoot = pkiRoot;
                o.RejectSHA1Certificates = true;
                o.MinCertificateKeySize = 2048;
                o.IncludeSignAndEncryptPolicies = true;
                o.IncludeUnsecurePolicyNone = opc.AllowInsecure;
                o.IncludeEccPolicies = false;
                // 사용자 토큰은 익명 — 접근 통제는 보안 채널의 클라이언트 인증서 신뢰다(계정 시스템 없음).
                o.UserTokenPolicies.Add(new OpcUaUserTokenPolicy { TokenType = UserTokenType.Anonymous });
                o.EndpointUrls.Add($"opc.tcp://{host}:{opc.Port}/UVision");
            })
            .AddDefaultIdentityAuthenticators(a =>
            {
                a.EnableAnonymous = true;
                a.EnableUserNamePassword = false;
                a.EnableX509 = false;
                a.EnableJwt = false;
            });
        return services;
    }

    // 잘못된 설정은 기동에서 멈춘다 — 켜 놓았는데 조용히 안 서는 것보다 낫다.
    private static void Validate(OpcUaOptions opc)
    {
        if (opc.Port is < 1 or > 65535)
            throw new InvalidOperationException("UVision:Events:OpcUa:Port 는 1~65535 여야 한다");
        if (opc.ResultWindow is < 1 or > 100_000)
            throw new InvalidOperationException("UVision:Events:OpcUa:ResultWindow 는 1~100000 여야 한다");
    }
}
