import type { UplatformAffiliationAttributes } from '@uplatform/brand'

// U-Platform 소속 표시 엘리먼트를 JSX 에 알린다(패키지는 DOM 타입만 싣는다).
declare module 'react' {
  namespace JSX {
    interface IntrinsicElements {
      'uplatform-affiliation': React.HTMLAttributes<HTMLElement> & UplatformAffiliationAttributes
    }
  }
}
