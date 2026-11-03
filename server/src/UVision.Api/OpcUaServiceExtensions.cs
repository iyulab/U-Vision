using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Opc.Ua;
using Opc.Ua.Server.Hosting;
using UVision.Api.Configuration;
using UVision.Api.OpcUa;
using UVision.Api.Services.Events;
using UVision.Api.Storage;

namespace UVision.Api;

/// <summary>
/// OPC UA 서버(P4 사건 전송) — 엔진 호스트에만 있다. 게시 패키지(<c>UVision.Hosting</c>)는 서버 스택을 싣지 않는다.
/// 기본 꺼짐: 설정이 없으면 아무것도 등록하지 않아 포트도 인증서 폴더도 생기지 않는다.
/// </summary>
public static class OpcUaServiceExtensions
{
    /// <summary>
    /// PKI 폴더(상대) — 데이터 루트 아래. 관찰한 배치: 서버 인스턴스 인증서는 <c>certs</c>(공개) · <c>private</c>(개인 키),
    /// 신뢰 목록에 없는 클라이언트의 인증서는 첫 접속 시도에서 스택이 <c>rejected/certs</c> 에 두며, 관리자가 그것을
    /// <c>trusted/certs</c>(없으면 만든다)로 복사하면 그 클라이언트가 접속된다.
    /// </summary>
    public const string PkiFolder = "opcua/pki";

    public static IServiceCollection AddUVisionOpcUa(
        this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        var options = configuration.GetSection(UVisionOptions.SectionName).Get<UVisionOptions>() ?? new UVisionOptions();
        var opc = options.Events.OpcUa;
        Validate(opc);
        if (!opc.Enabled)
            return services;

        var paths = new StoragePaths(options.Storage, contentRoot);
        var pkiRoot = Path.Combine(paths.Root, PkiFolder);
        var host = string.IsNullOrWhiteSpace(opc.Host) ? Dns.GetHostName() : opc.Host.Trim();

        // 결과 보관소(최근 결과 창 + 서 있는 노드 관리자) — 사건 전송과 서버 스택이 이것을 거쳐 닿는다.
        var hub = new OpcUaResultHub(opc.ResultWindow);
        services.AddSingleton(hub);
        // 사건의 받는 곳으로 더한다 — 꺼져 있으면 여기까지 오지 않아 아무것도 등록되지 않는다(기존 동작 byte 동일).
        services.AddSingleton<IEventDestinationSource>(
            new OpcUaDestinationSource(hub, new ScenarioFactsReader(paths)));

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
            })
            .AddNodeManager(new ResultsNodeManagerFactory(hub));
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
