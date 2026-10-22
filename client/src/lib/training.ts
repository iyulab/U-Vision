import type { TrainingReadiness } from './types'

/** 학습 준비 신호를 한 줄로 — 메트릭 탭이 보인다. `ready` 는 관리자가 할 일이 생긴 상태다. */
export function readinessLine(r: TrainingReadiness): { tone: 'ready' | 'pending'; text: string } {
  const perClass = r.classes.map((c) => `${c.id} ${c.count}/${r.min_per_class}`).join(' · ')
  if (r.active_version === undefined) {
    return r.build_ready
      ? { tone: 'ready', text: `첫 전용 모델을 빌드할 수 있습니다 — ${perClass}` }
      : { tone: 'pending', text: `전용 모델 빌드까지 사람 확인 라벨이 더 필요합니다 — ${perClass}` }
  }
  if (r.new_labels_since_model === undefined)
    return { tone: 'pending', text: `모델 ${r.active_version} — 학습한 데이터셋 기록이 없어 새 라벨을 셀 수 없습니다` }
  return r.rebuild_suggested
    ? {
        tone: 'ready',
        text: `모델 ${r.active_version} 이후 새 라벨 ${r.new_labels_since_model}건 — 재빌드를 제안합니다(기준 ${r.rebuild_min_new_labels}건)`,
      }
    : {
        tone: 'pending',
        text: `모델 ${r.active_version} 이후 새 라벨 ${r.new_labels_since_model}건(재빌드 기준 ${r.rebuild_min_new_labels}건)`,
      }
}
