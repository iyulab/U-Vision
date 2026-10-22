// Every sentence the site states about the product, in both languages. Each one that makes a claim
// carries the id of its row in ../claims.tsv, which records the claim's status and where this
// repository backs it — test/claims.test.ts keeps the two in step.

export type Locale = 'ko' | 'en';

/** Every page of the site, in both languages — the address each language version lives at. */
export const PATHS = {
  home: { ko: '/', en: '/en/' },
} as const;

export type PageId = keyof typeof PATHS;

export const SITE_URL = 'https://vision.u-platform.kr';
export const SOURCE_URL = 'https://github.com/iyulab/U-Vision';
export const QUICKSTART_URL = 'https://github.com/iyulab/U-Vision#readme';
export const API_URL = 'https://github.com/iyulab/U-Vision/blob/main/server/openapi/u-vision.json';
export const NUGET_URL = 'https://www.nuget.org/packages/UVision.Hosting';
export const CONTACT_URL = 'https://u-platform.kr/contact';

export interface Section {
  claim: string;
  heading: string;
  body: string;
}

export interface Copy {
  title: string;
  description: string;
  headline: string;
  lede: { claim: string; text: string };
  start: string;
  readSource: string;
  sections: Section[];
  delivery: { heading: string; items: Section[] };
  notHeading: string;
  not: Section[];
  license: Section;
  footer: { source: string; api: string; nuget: string; contact: string };
  otherLanguage: string;
}

export const COPY: Record<Locale, Copy> = {
  ko: {
    title: 'U-Vision — 셀프호스트 비전 판정',
    description:
      '태블릿 카메라로 찍고, 직접 운영하는 비전 모델이 시나리오 기준으로 OK/NG 를 판정합니다. 이미지는 공장을 떠나지 않습니다.',
    headline: '찍고, 판정하고, 기록한다 — 우리 서버에서',
    lede: {
      claim: 'lede',
      text: '검사 구역에 부품을 올려놓으면 움직임이 멈추는 순간 태블릿이 촬영하고, 서버의 비전 모델이 시나리오에 적은 기준과 기준 이미지로 OK/NG 와 소견을 냅니다. 앱 설치 없이 브라우저로 씁니다.',
    },
    start: '설치 안내',
    readSource: '소스 보기',
    sections: [
      {
        claim: 'capture',
        heading: '멈추면 찍는다',
        body: '움직임 감지는 기기 안에서 돌고, 흐린 사진은 서버로 보내기 전에 거릅니다. 수동 셔터도 있습니다.',
      },
      {
        claim: 'verify',
        heading: '오투입 확인',
        body: '작업자가 기대 품번을 넣고 찍으면 일치/불일치로 판정합니다. 기기가 바코드를 읽을 수 있으면 읽은 코드와 먼저 대조합니다.',
      },
      {
        claim: 'offline',
        heading: '네트워크가 끊겨도',
        body: '캡처는 먼저 기기에 저장되고, 서버에 닿지 못하면 연결이 돌아왔을 때 찍은 순서대로 보냅니다. 기록에는 실제 촬영 시각이 남습니다.',
      },
      {
        claim: 'crosscheck',
        heading: '두 번째 판정과 사람의 검토',
        body: '전용 ML 분류기를 붙이면 비전 모델과 나란히 판정하고, 둘이 다르거나 확신이 낮은 건을 검토 목록에 올립니다. 사람이 단 라벨은 다음 학습 데이터가 됩니다.',
      },
      {
        claim: 'failclosed',
        heading: '모르면 모른다고',
        body: '비전 모델을 쓸 수 없을 때는 자동으로 OK 를 내지 않고 «판정 불가 — 사람 확인»을 띄웁니다.',
      },
    ],
    delivery: {
      heading: '운영 방식',
      items: [
        {
          claim: 'selfhost',
          heading: '셀프호스트',
          body: '판정 기록과 이미지는 서버의 파일 시스템에 남습니다. 데이터베이스 서버가 필요 없고, 백업은 폴더 복사입니다.',
        },
        {
          claim: 'models',
          heading: '직접 운영하는 비전 모델',
          body: 'OpenAI 호환 API 로 서비스하는 자체 GPU 서버의 모델로 판정합니다 — 검사 이미지가 외부로 나가지 않습니다.',
        },
        {
          claim: 'embed',
          heading: '기존 시스템 안에',
          body: '단독 서버로도, ASP.NET Core 앱에 NuGet 패키지로 끼워서도 씁니다.',
        },
        {
          claim: 'integrate',
          heading: '다른 시스템과',
          body: 'API 는 OpenAPI 문서로 공개되어 있고, 판정마다 서명된 웹훅(CloudEvents)을 MES 같은 시스템으로 보낼 수 있습니다.',
        },
      ],
    },
    notHeading: '하지 않는 것',
    not: [
      {
        claim: 'no-accuracy-promise',
        heading: '정확도를 약속하지 않습니다.',
        body: '판정 품질은 부품 · 모델 · 기준에 달렸습니다. 그래서 사람 라벨과 판정 지표를 남겨 현장에서 재게 합니다.',
      },
      {
        claim: 'no-accounts',
        heading: '계정 시스템이 없습니다.',
        body: '한 조직이 직접 운영하는 것을 전제로, 작업 화면은 열려 있고 설정 변경만 관리자 PIN 으로 막습니다.',
      },
      {
        claim: 'no-cookies',
        heading: '이 사이트는 쿠키를 쓰지 않습니다.',
        body: '추적 스크립트도 없습니다.',
      },
    ],
    license: {
      claim: 'license',
      heading: '라이선스',
      body: 'MIT 라이선스의 오픈소스입니다.',
    },
    footer: { source: 'GitHub', api: 'API(OpenAPI)', nuget: 'NuGet', contact: '문의' },
    otherLanguage: 'English',
  },
  en: {
    title: 'U-Vision — self-hosted vision judgment',
    description:
      'A tablet camera takes the picture and a vision model you run judges it OK or NG against your scenario. Images stay in the plant.',
    headline: 'Capture, judge and record — on your own server',
    lede: {
      claim: 'lede',
      text: 'Place a part in the inspection area and the tablet takes the picture the moment it holds still; the vision model on your server answers OK or NG with findings, using the criteria and reference images in the scenario. It runs in a browser, nothing to install.',
    },
    start: 'Get started',
    readSource: 'Read the source',
    sections: [
      {
        claim: 'capture',
        heading: 'It shoots when the part stops',
        body: 'Motion detection runs on the device, and blurry shots are rejected before they are sent. A manual shutter is there too.',
      },
      {
        claim: 'verify',
        heading: 'Part verification',
        body: 'The operator enters the expected part number and the capture is judged a match or a mismatch. When the device can read a barcode, the code read is compared first.',
      },
      {
        claim: 'offline',
        heading: 'When the network drops',
        body: 'Captures are saved on the device first; those that cannot reach the server are sent in the order taken once it is back, and the record keeps the time they were taken.',
      },
      {
        claim: 'crosscheck',
        heading: 'A second opinion and human review',
        body: 'Add a dedicated ML classifier and it judges alongside the vision model; disagreements and low-confidence results go to a review list. Labels people add become the next training data.',
      },
      {
        claim: 'failclosed',
        heading: 'It says when it cannot tell',
        body: 'When the vision model is unavailable it does not fall back to OK; it shows "unable to decide — check by hand".',
      },
    ],
    delivery: {
      heading: 'How it runs',
      items: [
        {
          claim: 'selfhost',
          heading: 'Self-hosted',
          body: 'Decisions and images are kept on the server\'s file system. There is no database server to run; a backup is a folder copy.',
        },
        {
          claim: 'models',
          heading: 'Models you run',
          body: 'It judges with a model served from your own GPU server through an OpenAI-compatible API, so inspection images do not leave your network.',
        },
        {
          claim: 'embed',
          heading: 'Inside what you already run',
          body: 'Run it as a standalone server, or mount it in an ASP.NET Core app as a NuGet package.',
        },
        {
          claim: 'integrate',
          heading: 'Talking to other systems',
          body: 'The API is described by a published OpenAPI document, and each decision can be posted to systems such as an MES as a signed webhook (CloudEvents).',
        },
      ],
    },
    notHeading: 'What it does not do',
    not: [
      {
        claim: 'no-accuracy-promise',
        heading: 'It does not promise accuracy.',
        body: 'Judgment quality depends on the part, the model and the criteria, so it keeps human labels and decision metrics for you to measure it on site.',
      },
      {
        claim: 'no-accounts',
        heading: 'There are no user accounts.',
        body: 'It assumes one organization runs it: the operator screen is open, and only configuration changes need the admin PIN.',
      },
      {
        claim: 'no-cookies',
        heading: 'This site sets no cookies.',
        body: 'There are no tracking scripts either.',
      },
    ],
    license: {
      claim: 'license',
      heading: 'License',
      body: 'Open source under the MIT license.',
    },
    footer: { source: 'GitHub', api: 'API (OpenAPI)', nuget: 'NuGet', contact: 'Contact' },
    otherLanguage: '한국어',
  },
};
