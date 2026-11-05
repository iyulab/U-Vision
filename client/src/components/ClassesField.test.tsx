import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { useState } from 'react'
import { afterEach, describe, expect, it } from 'vitest'

import { classDraftsOf, emptyClassDrafts, type ClassDraft } from '../lib/classification'
import { ClassesField } from './ClassesField'

afterEach(cleanup)

/** 상태를 쥐는 얇은 호스트 — 편집기는 제어 컴포넌트라 바깥이 줄을 들고 있어야 입력이 이어진다. */
function Host({ initial, onState }: { initial: ClassDraft[]; onState?: (c: ClassDraft[]) => void }) {
  const [classes, setClasses] = useState(initial)
  onState?.(classes)
  return <ClassesField classes={classes} onChange={setClasses} />
}

describe('ClassesField', () => {
  it('새 시나리오는 빈 줄 둘로 시작하고 id 가 비어 있다는 사유를 보인다', () => {
    render(<Host initial={emptyClassDrafts()} />)
    expect(screen.getAllByTestId('class-row')).toHaveLength(2)
    expect(screen.getByTestId('classes-error').textContent).toMatch(/빈 칸/)
  })

  it('id 를 채우면 사유가 사라지고, 클래스 추가 · 지우기로 줄 수가 바뀐다', () => {
    render(<Host initial={emptyClassDrafts()} />)
    fireEvent.change(screen.getByLabelText('클래스 1 id'), { target: { value: 'A100' } })
    fireEvent.change(screen.getByLabelText('클래스 2 id'), { target: { value: 'B200' } })
    expect(screen.queryByTestId('classes-error')).toBeNull()

    fireEvent.click(screen.getByText('+ 클래스 추가'))
    expect(screen.getAllByTestId('class-row')).toHaveLength(3)
    expect(screen.getByTestId('classes-error').textContent).toMatch(/빈 칸/)

    fireEvent.click(screen.getByLabelText('클래스 3 지우기'))
    expect(screen.getAllByTestId('class-row')).toHaveLength(2)
    expect(screen.queryByTestId('classes-error')).toBeNull()
  })

  it('둘 밖으로 지우면 «둘 이상» 사유가 보인다', () => {
    render(<Host initial={classDraftsOf({ type: 'classification', classes: [{ id: 'A' }, { id: 'B' }] })} />)
    fireEvent.click(screen.getByLabelText('클래스 2 지우기'))
    expect(screen.getByTestId('classes-error').textContent).toMatch(/둘 이상/)
  })

  it('확정 허용과 꼭 잡을 값은 서로를 잠근다(서버가 둘 다 켠 클래스를 거부한다)', () => {
    render(<Host initial={classDraftsOf({ type: 'classification', classes: [{ id: 'A' }, { id: 'B' }] })} />)
    const [settle1] = screen.getAllByLabelText('아래 단(전용 모델 · 기억)이 확정해도 됨') as HTMLInputElement[]
    const [catch1] = screen.getAllByLabelText('꼭 잡을 값(놓치면 안 됨)') as HTMLInputElement[]

    fireEvent.click(catch1)
    expect(catch1.checked).toBe(true)
    expect(settle1.disabled).toBe(true)

    fireEvent.click(catch1)
    expect(settle1.disabled).toBe(false)
    fireEvent.click(settle1)
    expect(catch1.disabled).toBe(true)
  })

  it('저장된 클래스의 id 는 고치지 못하고 새로 더한 줄의 id 는 고친다', () => {
    render(<Host initial={classDraftsOf({ type: 'classification', classes: [{ id: 'A' }, { id: 'B' }] })} />)
    expect((screen.getByLabelText('클래스 1 id') as HTMLInputElement).readOnly).toBe(true)
    expect(screen.getByText(/이미 있던 클래스를 지우면/)).toBeTruthy()

    fireEvent.click(screen.getByText('+ 클래스 추가'))
    expect((screen.getByLabelText('클래스 3 id') as HTMLInputElement).readOnly).toBe(false)
  })

  it('입력한 표시 이름 · 설명이 줄 상태에 그대로 실린다', () => {
    let latest: ClassDraft[] = []
    render(<Host initial={emptyClassDrafts()} onState={(c) => (latest = c)} />)
    fireEvent.change(screen.getByLabelText('클래스 1 표시 이름'), { target: { value: '품번 A' } })
    fireEvent.change(screen.getByLabelText('클래스 1 설명'), { target: { value: '금색 커넥터' } })
    expect(latest[0]).toMatchObject({ label: '품번 A', description: '금색 커넥터' })
  })
})
