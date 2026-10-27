import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { Reference } from '../lib/types'

const listReferences = vi.fn<(scenarioId: string) => Promise<Reference[]>>()
vi.mock('../lib/api', () => ({
  listReferences: (id: string) => listReferences(id),
  referenceUrl: () => '',
  uploadReference: vi.fn(),
  deleteReference: vi.fn(),
}))

import { ReferenceGallery } from './ReferenceGallery'

afterEach(() => {
  cleanup()
  listReferences.mockReset()
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
