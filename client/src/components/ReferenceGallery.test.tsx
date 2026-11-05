import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { Reference } from '../lib/types'

const listReferences = vi.fn<(scenarioId: string) => Promise<Reference[]>>()
const uploadReference = vi.fn()
const deleteReference = vi.fn()
vi.mock('../lib/api', () => ({
  listReferences: (id: string) => listReferences(id),
  referenceUrl: (id: string, label: string, ref: string) => `/ref/${id}/${label}/${ref}`,
  uploadReference: (...args: unknown[]) => uploadReference(...args),
  deleteReference: (...args: unknown[]) => deleteReference(...args),
}))

import { ReferenceGallery } from './ReferenceGallery'

afterEach(() => {
  cleanup()
  listReferences.mockReset()
  uploadReference.mockReset()
  deleteReference.mockReset()
})

const runWithPin = vi.fn()

describe('ReferenceGallery 기준 이미지 없음 안내', () => {
  it('검사 시나리오에 기준이 없으면 목록을 읽은 뒤 안내를 보인다', async () => {
    listReferences.mockResolvedValue([])
    render(<ReferenceGallery scenarioId="s1" runWithPin={runWithPin} scenarioType="inspection" />)
    // 목록을 읽기 전에는 깜빡이지 않는다.
    expect(screen.queryByText(/기준 이미지가 없습니다/)).toBeNull()
    expect(await screen.findByText(/기준 이미지가 없습니다/)).toBeTruthy()
  })

  it('기준이 있으면 안내하지 않는다', async () => {
    listReferences.mockResolvedValue([{ ref_id: 'r1', label: 0, ng_label: null }] as unknown as Reference[])
    render(<ReferenceGallery scenarioId="s1" runWithPin={runWithPin} scenarioType="inspection" />)
    await waitFor(() => expect(listReferences).toHaveBeenCalled())
    await waitFor(() => expect(screen.queryByText(/기준 이미지가 없습니다/)).toBeNull())
  })

  it('오투입 확인 시나리오는 기준이 없어도 안내하지 않는다', async () => {
    listReferences.mockResolvedValue([])
    render(<ReferenceGallery scenarioId="s1" runWithPin={runWithPin} scenarioType="verification" />)
    await waitFor(() => expect(listReferences).toHaveBeenCalled())
    await new Promise((resolve) => setTimeout(resolve, 0)) // 목록 상태 반영을 기다린 뒤에도
    expect(screen.queryByText(/기준 이미지가 없습니다/)).toBeNull()
  })
})

describe('ReferenceGallery 다중 분류(classification)', () => {
  const classes = [{ id: 'A100', label: '품번 A' }, { id: 'B200' }, { id: 'reject', must_catch: true }]
  const wire = [
    { ref_id: 'r1', label: 0, ng_label: null, class: 'A100' },
    { ref_id: 'r2', label: 1, ng_label: null, class: 'reject' },
  ] as unknown as Reference[]
  // 같은 호출 모양 — 업로드 · 삭제는 PIN 러너를 거치므로 러너가 액션에 PIN 을 넘겨 부르게 한다.
  const pinRunner = vi.fn(async (action: (pin: string) => Promise<void>) => {
    await action('1234')
    return 'ok' as const
  })

  it('OK/NG 두 구획이 아니라 클래스마다 구획을 그리고, 이미지 없는 클래스를 이름으로 안내한다', async () => {
    listReferences.mockResolvedValue(wire)
    render(<ReferenceGallery scenarioId="s1" runWithPin={pinRunner} scenarioType="classification" classes={classes} />)
    const gallery = await screen.findByTestId('class-reference-gallery')
    expect(within(gallery).getByText('품번 A')).toBeTruthy() // 표시 이름이 제목, id 는 보조 글
    expect(within(gallery).getByText('A100')).toBeTruthy()
    expect(within(gallery).getByText('B200')).toBeTruthy()
    expect(within(gallery).queryByText('OK 기준')).toBeNull()
    expect(await within(gallery).findByText(/기준 이미지가 없는 클래스: B200/)).toBeTruthy()
    // 이미지는 클래스 id 를 경로로 서빙된다.
    const srcs = within(gallery).getAllByRole('img').map((i) => i.getAttribute('src'))
    expect(srcs).toEqual(['/ref/s1/A100/r1', '/ref/s1/reject/r2'])
  })

  it('업로드는 그 구획의 클래스 id 로 가고 ng_label 은 보내지 않는다', async () => {
    listReferences.mockResolvedValue([])
    uploadReference.mockResolvedValue({})
    const { container } = render(
      <ReferenceGallery scenarioId="s1" runWithPin={pinRunner} scenarioType="classification" classes={classes} />,
    )
    await screen.findByTestId('class-reference-gallery')
    const inputs = container.querySelectorAll('input[type="file"]')
    expect(inputs).toHaveLength(3)
    const file = new File(['x'], 'b.png', { type: 'image/png' })
    fireEvent.change(inputs[1], { target: { files: [file] } })
    await waitFor(() => expect(uploadReference).toHaveBeenCalledWith('s1', file, 'B200', undefined, '1234'))
  })

  it('삭제는 그 이미지가 속한 클래스 id 경로로 간다', async () => {
    listReferences.mockResolvedValue(wire)
    deleteReference.mockResolvedValue(undefined)
    render(<ReferenceGallery scenarioId="s1" runWithPin={pinRunner} scenarioType="classification" classes={classes} />)
    await screen.findByTestId('class-reference-gallery')
    const [deleteA] = await screen.findAllByLabelText('삭제')
    fireEvent.click(deleteA)
    await waitFor(() => expect(deleteReference).toHaveBeenCalledWith('s1', 'A100', 'r1', '1234'))
  })

  it('선언에서 빠진 클래스의 이미지는 개수만 알린다', async () => {
    listReferences.mockResolvedValue([{ ref_id: 'r9', label: 0, ng_label: null, class: 'gone' }] as unknown as Reference[])
    render(<ReferenceGallery scenarioId="s1" runWithPin={pinRunner} scenarioType="classification" classes={classes} />)
    expect(await screen.findByText(/선언에서 빠진 클래스의 기준 이미지 1장/)).toBeTruthy()
  })
})
