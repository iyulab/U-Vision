// Every sentence the site states about the product, in both languages. Each one that makes a claim
// carries the id of its row in ../claims.tsv, which records the claim's status and where this
// repository backs it — test/claims.test.ts keeps the two in step. A claim whose status is `vision`
// is a goal, not built yet: the page says it in the future tense and offers to take requests for it (`roadmap: true`
// here). The page carries no «in development» label — its reader is a plant's adopter, not a developer.

import type { SlotId } from './media';

export type Locale = 'ko' | 'en';

/** Every page of the site, in both languages — the address each language version lives at. */
export const PATHS = {
  home: { ko: '/', en: '/en/' },
} as const;

export type PageId = keyof typeof PATHS;

export const SITE_URL = 'https://vision.u-platform.kr';
export const SOURCE_URL = 'https://github.com/iyulab/U-Vision';
export const API_URL = 'https://github.com/iyulab/U-Vision/blob/main/server/openapi/u-vision.json';
export const NUGET_URL = 'https://www.nuget.org/packages/UVision.Hosting';
export const CONTACT_URL = 'https://u-platform.kr/contact';

/**
 * A link to the contact form. The form fills in the product from `product` and the request type from `intent`;
 * `topic` names the part of this page the visitor asked from — a roadmap claim id, or the section — so requests say
 * which roadmap item they are about. The link carries no visitor data: it is the same for everyone.
 */
export function contactUrl(topic: string, intent: 'general' | 'demo' = 'general'): string {
  return `${CONTACT_URL}?${new URLSearchParams({ product: 'u-vision', intent, topic })}`;
}

/** One statement the site makes, tied to its row in claims.tsv. */
export interface Point {
  claim: string;
  text: string;
  roadmap?: boolean;
}

export interface Item {
  claim: string;
  heading: string;
  body: string;
  roadmap?: boolean;
}

/** A section of the page built around one screen. */
export interface Feature {
  id: string;
  media: SlotId;
  heading: string;
  body: Point;
  points: Point[];
}

export interface Scenario extends Item {
  media: SlotId;
}

export interface Copy {
  title: string;
  description: string;
  nav: { screens: string; learning: string; scenarios: string; start: string; contact: string };
  hero: { headline: string; lede: Point; cta: string; secondary: string; pause: string; play: string };
  /** The link beside a roadmap item that asks for it. */
  ask: string;
  values: { heading: string; items: Item[] };
  features: Feature[];
  scenarios: { heading: string; intro: Point; edge: Point; items: Scenario[] };
  integration: {
    heading: string;
    body: Point;
    points: Point[];
    diagram: {
      label: string;
      tablet: string;
      camera: string;
      engine: string;
      engineSub: string;
      model: string;
      mes: string;
      webhook: string;
      protocols: string;
      orders: string;
    };
  };
  delivery: { heading: string; items: Item[] };
  adoption: { heading: string; claim: string; steps: { heading: string; body: string }[] };
  demand: { heading: string; body: string; cta: string };
  /** One line under the page: the license, and a link to the source. */
  oss: { claim: string; text: string; link: string };
  notHeading: string;
  not: Item[];
  footer: { source: string; api: string; nuget: string; contact: string };
  otherLanguage: string;
}

export const COPY: Record<Locale, Copy> = {
  ko: {
    title: 'U-Vision — 셀프호스트 비전 검사',
    description:
      '태블릿 카메라 하나로 시작하는 비전 검사. 부품을 올려놓으면 멈추는 순간 찍고, 직접 운영하는 비전 모델이 우리 기준대로 판정하고 기록합니다. 사진은 공장을 떠나지 않습니다.',
    nav: { screens: '화면', learning: '학습', scenarios: '시나리오', start: '도입', contact: '도입 상담' },
    hero: {
      headline: '눈으로 하던 검사를, 기록이 남는 판정으로',
      lede: {
        claim: 'lede',
        text: '검사대에 부품을 올려놓으면 움직임이 멈추는 순간 태블릿이 찍고, 우리 서버의 비전 모델이 시나리오에 적은 기준과 기준 사진으로 OK·NG 와 불량 유형, 소견을 냅니다. 앱 설치 없이 브라우저로 씁니다.',
      },
      cta: '도입 상담',
      secondary: '화면으로 보기',
      pause: '영상 멈춤',
      play: '영상 재생',
    },
    ask: '필요하면 알려 주세요',
    values: {
      heading: '무엇이 달라지나',
      items: [
        {
          claim: 'value-criteria',
          heading: '기준이 숙련자 머리에서 시나리오로',
          body: '라인마다 검사 기준을 글과 OK·NG 기준 사진으로 적어 둡니다. 사람이 바뀌어도 같은 기준으로 판정합니다.',
        },
        {
          claim: 'value-record',
          heading: '모든 판정이 사진과 함께 남습니다',
          body: '무엇을 왜 NG 로 봤는지 사진, 소견, 신뢰도가 날짜별로 쌓입니다. 문제가 생긴 날의 사진을 다시 찾을 수 있습니다.',
        },
        {
          claim: 'value-learn',
          heading: '쓸수록 그 라인에 맞춰집니다',
          body: '사람이 확인한 결과는 그 라인 전용 모델의 학습 데이터가 되고, 전용 모델은 기록으로 실력을 보인 만큼 판정을 넘겨받습니다.',
        },
      ],
    },
    features: [
      {
        id: 'operator',
        media: 'operator',
        heading: '멈추면 찍고, 바로 판정합니다',
        body: {
          claim: 'capture',
          text: '작업자는 부품을 올려놓기만 합니다. 움직임 감지와 흐린 사진 거르기는 태블릿 안에서 돌고, 결과는 멀리서도 보이는 크기로 나옵니다. 수동 셔터도 있습니다.',
        },
        points: [
          { claim: 'defect-type', text: 'NG 는 긁힘·찍힘·변색처럼 시나리오에 정한 불량 유형으로 나눠 판정하고, 위치를 적은 소견을 붙입니다.' },
          { claim: 'failclosed', text: '비전 모델에 닿을 수 없으면 OK 를 내지 않고 «판정 불가 — 사람 확인»을 띄웁니다.' },
        ],
      },
      {
        id: 'verify',
        media: 'verify',
        heading: '다른 부품이 들어오면 막습니다',
        body: {
          claim: 'verify',
          text: '작업자가 기대 품번을 넣고 찍으면 일치·불일치로 답합니다. 태블릿이 라벨의 바코드를 읽을 수 있으면 읽은 코드로 먼저 대조하고, 코드가 없으면 비전 모델이 기대 품번을 들고 확인합니다.',
        },
        points: [],
      },
      {
        id: 'offline',
        media: 'offline',
        heading: '네트워크가 끊겨도 놓치지 않습니다',
        body: {
          claim: 'offline',
          text: '캡처는 태블릿에 먼저 저장됩니다. 서버에 닿지 못하면 연결이 돌아왔을 때 찍은 순서대로 보내고, 같은 사진은 한 번만 판정됩니다. 기록에는 실제 촬영 시각이 남습니다.',
        },
        points: [],
      },
      {
        id: 'console',
        media: 'console',
        heading: '관리자는 콘솔에서 확인합니다',
        body: {
          claim: 'review',
          text: '날짜와 시나리오로 결과를 찾고, 비전 모델과 전용 모델의 답이 다르거나 확신이 낮은 건은 검토 목록에서 사람이 OK·NG 를 확정합니다.',
        },
        points: [{ claim: 'export', text: '하루 요약과 불량 유형별 건수를 보고, 결과를 CSV 로 내려받습니다.' }],
      },
      {
        id: 'learning',
        media: 'learning',
        heading: '쓸수록 똑똑해지는 판정',
        body: {
          claim: 'readiness',
          text: '첫날은 비전 모델이 판정합니다. 사람이 확인한 사진이 유형마다 모이면 콘솔이 그 라인 전용 모델을 만들 때가 됐다고 알리고, 학습에 쓸 사진도 정해진 기준대로 골라 묶어 줍니다.',
        },
        points: [
          { claim: 'crosscheck', text: '전용 모델은 비전 모델과 나란히 판정하고, 둘의 답이 다르면 검토 목록에 올립니다.' },
          { claim: 'authority', text: '전용 모델은 기록만 하는 단계에서 시작해, 실력을 보인 만큼 관리자가 한 단계씩 판정을 넘깁니다. 틀리기 시작하면 시스템이 자동으로 한 단계 내립니다.' },
          { claim: 'memory', text: '사람이 확정한 사례를 기억해, 비슷한 건은 모델 학습을 기다리지 않고 바로 판정하도록 만들고 있습니다.', roadmap: true },
        ],
      },
    ],
    scenarios: {
      heading: '하나의 엔진으로 여러 검사를',
      intro: {
        claim: 'scenarios',
        text: '정지 사진 검사를 넘어, 라인 위 고정 카메라 영상에서도 같은 방식으로 판정하고 기록하도록 만들고 있습니다. 화재·연기 조기경보도 같은 길에 있습니다.',
        roadmap: true,
      },
      edge: {
        claim: 'edge',
        text: '영상은 라인 옆 엣지 장치가 처리하게 됩니다. 서버로는 판정할 사건과 잘라 낸 사진만 보내고, 연결이 끊기면 장치에 쌓았다가 다시 보냅니다.',
        roadmap: true,
      },
      items: [
        {
          claim: 'scenario-counting',
          media: 'counting',
          heading: '수량 세기',
          body: '컨베이어의 물건이 통과선을 지날 때마다 세어 작업지시 계획과 나란히 보여 주게 됩니다. 겹쳐 지나간 건은 다시 확인합니다.',
          roadmap: true,
        },
        {
          claim: 'scenario-safety',
          media: 'safety',
          heading: '안전모 착용 감시',
          body: '감시 구역 안 작업자의 안전모 착용을 보게 됩니다. 한 장면이 아니라 몇 초간 이어질 때만 알리고, 얼굴은 판정하지 않습니다.',
          roadmap: true,
        },
        {
          claim: 'scenario-anomaly',
          media: 'anomaly',
          heading: '이상 감지',
          body: '불량 사진이 없어도 시작할 수 있게 됩니다. 정상 사진만으로 다른 곳을 찾아 표시하고, 비전 모델이 무엇이 다른지 설명합니다.',
          roadmap: true,
        },
      ],
    },
    integration: {
      heading: '현장 시스템과 이어집니다',
      body: {
        claim: 'integrate',
        text: '판정 결과는 나오는 즉시 MES 같은 현장 시스템으로 전해지고, 위조를 막는 서명이 붙습니다. 연동 방법이 문서로 공개되어 있어 전산 담당자가 바로 이을 수 있습니다.',
      },
      points: [
        { claim: 'mes-in', text: 'MES 에서 작업지시·로트·기대 품번을 받아 오투입 확인에 바로 쓰도록 만들고 있습니다.', roadmap: true },
        { claim: 'industrial-protocols', text: '결과를 MQTT 와 OPC UA 로도 내보내도록 만들고 있습니다.', roadmap: true },
      ],
      diagram: {
        label: 'U-Vision 이 현장 장치, 비전 모델, MES 와 주고받는 것',
        tablet: '태블릿 · 정지 촬영',
        camera: '고정 카메라 · 엣지 장치',
        engine: 'U-Vision 엔진',
        engineSub: '우리 서버 · 판정 · 기록',
        model: '비전 모델 · 자체 GPU',
        mes: 'MES · 설비',
        webhook: '판정 결과 전달',
        protocols: 'MQTT · OPC UA',
        orders: '작업지시 · 기대 품번',
      },
    },
    delivery: {
      heading: '우리 서버에서 돌아갑니다',
      items: [
        {
          claim: 'models',
          heading: '사진이 공장을 떠나지 않습니다',
          body: '회사 안의 GPU 서버에서 돌리는 비전 모델로 판정합니다. 검사 사진을 외부 서비스로 보내지 않습니다.',
        },
        {
          claim: 'selfhost',
          heading: '데이터베이스 서버가 필요 없습니다',
          body: '판정 기록과 사진은 서버의 폴더에 남습니다. 백업은 폴더 복사입니다.',
        },
        {
          claim: 'service',
          heading: '공장 안에 구축하고 유지보수합니다',
          body: 'iyulab 이 회사 안 서버에 설치하고, 이후 유지보수까지 함께 맡습니다. 다른 시스템과는 서로 독립된 앱으로 두고 연동합니다.',
        },
      ],
    },
    adoption: {
      heading: '이렇게 시작합니다',
      claim: 'adoption',
      steps: [
        { heading: '검사 하나를 고릅니다', body: '효과가 큰 검사 한 가지를 정하고, 기준을 글과 OK·NG 기준 사진 몇 장으로 적습니다.' },
        { heading: '태블릿 한 대로 돌려 봅니다', body: '검사대 위에 태블릿을 두고 실제 라인에서 판정합니다. 처음에는 사람이 결과를 확인합니다.' },
        { heading: '기록으로 잽니다', body: '사람이 확인한 결과와 판정이 얼마나 맞았는지 콘솔에서 봅니다.' },
        { heading: '넘기고 넓힙니다', body: '데이터가 쌓이면 전용 모델을 만들어 판정을 넘기고, 다른 라인과 검사로 넓힙니다.' },
      ],
    },
    demand: {
      heading: '필요한 검사를 알려 주세요',
      body: '여기 없는 검사도 좋습니다. 어떤 부품을, 어떤 기준으로, 하루 몇 건 보는지 알려 주시면 맞는 시작점을 함께 찾습니다. 무엇을 먼저 만들지도 이런 요청으로 정합니다.',
      cta: '도입 상담 요청',
    },
    oss: {
      claim: 'license',
      text: 'AGPL-3.0 오픈소스 — AGPL 조건으로 쓰기 어려운 조직에는 상용 라이선스를 드립니다.',
      link: 'GitHub',
    },
    notHeading: '하지 않는 것',
    not: [
      {
        claim: 'no-accuracy-promise',
        heading: '정확도를 미리 약속하지 않습니다.',
        body: '판정 품질은 부품과 모델과 기준에 달렸습니다. 그래서 사람 확인과 판정 지표를 남겨 현장에서 재게 합니다.',
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
    footer: { source: 'GitHub', api: 'API(OpenAPI)', nuget: 'NuGet', contact: '문의' },
    otherLanguage: 'English',
  },
  en: {
    title: 'U-Vision — self-hosted vision inspection',
    description:
      'Vision inspection that starts with one tablet camera. Set a part down and it shoots the moment the part holds still; a vision model you run judges it against your criteria and keeps the record. Images stay in the plant.',
    nav: { screens: 'Screens', learning: 'Learning', scenarios: 'Scenarios', start: 'Getting started', contact: 'Talk to us' },
    hero: {
      headline: 'Turn inspection by eye into decisions you can look up',
      lede: {
        claim: 'lede',
        text: 'Set a part on the tray and the tablet shoots the moment it holds still; the vision model on your server answers OK or NG with the defect type and a finding, using the criteria and reference images in the scenario. It runs in a browser, nothing to install.',
      },
      cta: 'Talk to us',
      secondary: 'See the screens',
      pause: 'Pause video',
      play: 'Play video',
    },
    ask: 'Need this? Tell us',
    values: {
      heading: 'What changes',
      items: [
        {
          claim: 'value-criteria',
          heading: 'Criteria move from people\'s heads into scenarios',
          body: 'Each line writes its criteria down as text and OK and NG reference images. When people change, the criteria do not.',
        },
        {
          claim: 'value-record',
          heading: 'Every decision is kept with its image',
          body: 'What was called NG and why — image, finding and confidence — is filed by date. You can find the images from the day a problem started.',
        },
        {
          claim: 'value-learn',
          heading: 'It fits the line better the longer you use it',
          body: 'Results people confirm become training data for a model dedicated to that line, and that model takes over judgment as far as its record earns.',
        },
      ],
    },
    features: [
      {
        id: 'operator',
        media: 'operator',
        heading: 'It shoots when the part stops and answers at once',
        body: {
          claim: 'capture',
          text: 'The operator only sets the part down. Motion detection and blur rejection run on the tablet, and the result is shown large enough to read from across the station. A manual shutter is there too.',
        },
        points: [
          { claim: 'defect-type', text: 'An NG is sorted into the defect types the scenario declares — scratch, dent, discoloration — with a finding that says where.' },
          { claim: 'failclosed', text: 'When the vision model cannot be reached it does not fall back to OK; it shows "unable to decide — check by hand".' },
        ],
      },
      {
        id: 'verify',
        media: 'verify',
        heading: 'It stops the wrong part',
        body: {
          claim: 'verify',
          text: 'The operator enters the expected part number and the capture is answered as a match or a mismatch. When the tablet can read the label\'s barcode the code is compared first; without one, the vision model checks the part against the number.',
        },
        points: [],
      },
      {
        id: 'offline',
        media: 'offline',
        heading: 'Nothing is lost when the network drops',
        body: {
          claim: 'offline',
          text: 'Captures are saved on the tablet first. Those that cannot reach the server are sent in the order taken once it is back, each judged only once, and the record keeps the time they were taken.',
        },
        points: [],
      },
      {
        id: 'console',
        media: 'console',
        heading: 'Supervisors check it in the console',
        body: {
          claim: 'review',
          text: 'Find results by date and scenario. When the vision model and the dedicated model disagree, or confidence is low, a person settles OK or NG from the review list.',
        },
        points: [{ claim: 'export', text: 'See the day\'s summary and counts by defect type, and download results as CSV.' }],
      },
      {
        id: 'learning',
        media: 'learning',
        heading: 'Judgment that gets better with use',
        body: {
          claim: 'readiness',
          text: 'On day one the vision model judges. Once enough human-confirmed images of each type are in, the console says it is time to build a model dedicated to the line, and picks out and bundles the images to train it on by a set rule.',
        },
        points: [
          { claim: 'crosscheck', text: 'The dedicated model judges alongside the vision model, and disagreements go to the review list.' },
          { claim: 'authority', text: 'It starts by being recorded only; as its record earns it, a supervisor hands it judgment one stage at a time. When it starts to miss, the system steps it down on its own.' },
          { claim: 'memory', text: 'We are building a memory of confirmed cases, so similar parts are judged at once without waiting for training.', roadmap: true },
        ],
      },
    ],
    scenarios: {
      heading: 'One engine, many inspections',
      intro: {
        claim: 'scenarios',
        text: 'Beyond still images, we are building the same judge-and-record loop for fixed cameras watching the line. Early fire and smoke warning is on the same path.',
        roadmap: true,
      },
      edge: {
        claim: 'edge',
        text: 'Video will be processed by an edge device beside the line, which sends the server only the events to judge and the crops that go with them, holding them while the connection is down.',
        roadmap: true,
      },
      items: [
        {
          claim: 'scenario-counting',
          media: 'counting',
          heading: 'Counting',
          body: 'It will count items as they cross a line on the conveyor, beside the work order\'s plan. Items that pass overlapped are checked again.',
          roadmap: true,
        },
        {
          claim: 'scenario-safety',
          media: 'safety',
          heading: 'Hard-hat watch',
          body: 'It will watch for hard hats on workers inside a zone, alerting only when a miss lasts several seconds, not on a single frame, and never judging faces.',
          roadmap: true,
        },
        {
          claim: 'scenario-anomaly',
          media: 'anomaly',
          heading: 'Anomaly detection',
          body: 'It will start without any defect images, marking where a part differs from normal ones while the vision model explains what is different.',
          roadmap: true,
        },
      ],
    },
    integration: {
      heading: 'It connects to the systems on site',
      body: {
        claim: 'integrate',
        text: 'Each decision is passed to plant systems such as an MES the moment it is made, signed so it cannot be forged. How to connect is documented in the open, so your IT staff can wire it up directly.',
      },
      points: [
        { claim: 'mes-in', text: 'We are building intake of work orders, lots and expected part numbers from the MES, for the wrong-part check to use directly.', roadmap: true },
        { claim: 'industrial-protocols', text: 'We are building MQTT and OPC UA output for results.', roadmap: true },
      ],
      diagram: {
        label: 'What U-Vision exchanges with devices on site, the vision model and the MES',
        tablet: 'Tablet · still capture',
        camera: 'Fixed camera · edge device',
        engine: 'U-Vision engine',
        engineSub: 'Your server · judge · record',
        model: 'Vision model · your GPU',
        mes: 'MES · equipment',
        webhook: 'Decisions sent out',
        protocols: 'MQTT · OPC UA',
        orders: 'Work orders · part numbers',
      },
    },
    delivery: {
      heading: 'It runs on your server',
      items: [
        {
          claim: 'models',
          heading: 'Images stay in the plant',
          body: 'It judges with a vision model running on a GPU server inside your company. Inspection images are not sent to outside services.',
        },
        {
          claim: 'selfhost',
          heading: 'No database server to run',
          body: 'Decisions and images are kept in folders on the server. A backup is a folder copy.',
        },
        {
          claim: 'service',
          heading: 'Built inside your plant, and kept running',
          body: 'iyulab installs it on a server inside your company and looks after it afterwards. It runs as an application of its own and connects to your other systems.',
        },
      ],
    },
    adoption: {
      heading: 'How to start',
      claim: 'adoption',
      steps: [
        { heading: 'Pick one inspection', body: 'Choose one inspection where it would matter most, and write its criteria down as text and a few OK and NG reference images.' },
        { heading: 'Run it with one tablet', body: 'Put a tablet over the tray and judge on the real line. At first, people check the results.' },
        { heading: 'Measure it from the record', body: 'See in the console how often the decisions matched what people confirmed.' },
        { heading: 'Hand over and widen', body: 'As data builds up, train a dedicated model, hand judgment over, and extend to other lines and inspections.' },
      ],
    },
    demand: {
      heading: 'Tell us what you need to inspect',
      body: 'Even if it is not on this page. Tell us what parts, against what criteria and how many a day, and we will find a starting point together. Requests like yours decide what we build next.',
      cta: 'Request a consultation',
    },
    oss: {
      claim: 'license',
      text: 'Open source under AGPL-3.0 — a commercial license for organizations that cannot adopt AGPL terms.',
      link: 'GitHub',
    },
    notHeading: 'What it does not do',
    not: [
      {
        claim: 'no-accuracy-promise',
        heading: 'It does not promise accuracy up front.',
        body: 'Judgment quality depends on the part, the model and the criteria, so it keeps human confirmations and decision metrics for you to measure it on site.',
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
    footer: { source: 'GitHub', api: 'API (OpenAPI)', nuget: 'NuGet', contact: 'Contact' },
    otherLanguage: '한국어',
  },
};
