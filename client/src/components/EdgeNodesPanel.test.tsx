import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { PinRunResult } from '../hooks/usePin'
import type { EdgeNode, Scenario } from '../lib/types'

const listEdgeNodes = vi.fn<(pin: string) => Promise<EdgeNode[]>>()
const issueEdgeToken = vi.fn<(input: { name?: string; ttl_minutes?: number }, pin: string) => Promise<{ token: string; expires_at: string }>>()
const setEdgeNodeScenarios = vi.fn<(id: string, scenarios: string[], pin: string) => Promise<EdgeNode>>()
const revokeEdgeNode = vi.fn<(id: string, pin: string) => Promise<EdgeNode>>()

vi.mock('../lib/api', () => ({
  listEdgeNodes: (pin: string) => listEdgeNodes(pin),
  issueEdgeToken: (input: { name?: string; ttl_minutes?: number }, pin: string) => issueEdgeToken(input, pin),
  setEdgeNodeScenarios: (id: string, s: string[], pin: string) => setEdgeNodeScenarios(id, s, pin),
  revokeEdgeNode: (id: string, pin: string) => revokeEdgeNode(id, pin),
}))

import { EdgeNodesPanel } from './EdgeNodesPanel'

const scenarios = [
  { scenario_id: 'line1', name: '1라인' },
  { scenario_id: 'line2', name: '2라인' },
] as unknown as Scenario[]

function node(over: Partial<EdgeNode> = {}): EdgeNode {
  return {
    node_id: 'node_aaaa',
    name: '3라인 젯슨',
    registered_at: new Date().toISOString(),
    scenarios: ['line1'],
    last_seen: new Date().toISOString(),
    status: { reported_at: new Date().toISOString(), outbox_depth: 12, outbox_bytes: 2048, dropped_crops: 0, parked: 0, agent_version: '0.0.0-dev' },
    ...over,
  }
}

const runWithPin = vi.fn<(action: (pin: string) => Promise<void>) => Promise<PinRunResult>>(async (action) => {
  await action('1234')
  return 'ok'
})

afterEach(() => {
  cleanup()
  for (const m of [listEdgeNodes, issueEdgeToken, setEdgeNodeScenarios, revokeEdgeNode, runWithPin]) m.mockReset()
  runWithPin.mockImplementation(async (action) => {
    await action('1234')
    return 'ok'
  })
})

describe('EdgeNodesPanel', () => {
  it('does not read the list (and does not ask for a PIN) until the operator asks', () => {
    render(<EdgeNodesPanel scenarios={scenarios} runWithPin={runWithPin} />)
    expect(runWithPin).not.toHaveBeenCalled()
    expect(screen.getByText(/불러오기를 누르면 PIN 을 묻습니다/)).toBeTruthy()
  })

  it('loads the nodes with the PIN and shows backlog, assignment and agent version', async () => {
    listEdgeNodes.mockResolvedValue([node()])
    render(<EdgeNodesPanel scenarios={scenarios} runWithPin={runWithPin} />)

    fireEvent.click(screen.getByRole('button', { name: '노드 불러오기' }))

    const card = await screen.findByRole('listitem', { name: '노드 3라인 젯슨' })
    expect(listEdgeNodes).toHaveBeenCalledWith('1234')
    expect(within(card).getByText('대기 12건 · 버린 크롭 0건 · 따로 둔 0건')).toBeTruthy()
    expect(within(card).getByText('배정: 1라인')).toBeTruthy() // 시나리오 id 가 아니라 이름
    expect(within(card).getByText('에이전트 0.0.0-dev')).toBeTruthy()
  })

  it('shows parked items as a loud flag so a human looks at them', async () => {
    listEdgeNodes.mockResolvedValue([
      node({ status: { reported_at: new Date().toISOString(), outbox_depth: 0, outbox_bytes: 0, dropped_crops: 3, parked: 2 } }),
    ])
    render(<EdgeNodesPanel scenarios={scenarios} runWithPin={runWithPin} />)
    fireEvent.click(screen.getByRole('button', { name: '노드 불러오기' }))

    expect(await screen.findByText(/따로 둔 항목 2건/)).toBeTruthy()
    expect(screen.getByText(/버린 크롭 3건 — 노드 디스크 상한/)).toBeTruthy()
  })

  it('says so when there are no nodes, pointing at the token form', async () => {
    listEdgeNodes.mockResolvedValue([])
    render(<EdgeNodesPanel scenarios={scenarios} runWithPin={runWithPin} />)
    fireEvent.click(screen.getByRole('button', { name: '노드 불러오기' }))
    expect(await screen.findByText(/등록된 노드가 없습니다/)).toBeTruthy()
  })

  it('issues a token with the typed name and TTL and shows the plain text once', async () => {
    issueEdgeToken.mockResolvedValue({ token: 'abcd1234.secretsecret', expires_at: '2026-11-04T13:00:00Z' })
    render(<EdgeNodesPanel scenarios={scenarios} runWithPin={runWithPin} />)

    fireEvent.change(screen.getByPlaceholderText('예: 3라인 젯슨'), { target: { value: '  4라인  ' } })
    fireEvent.click(screen.getByRole('button', { name: '토큰 발급' }))

    expect((await screen.findByTestId('issued-token')).textContent).toBe('abcd1234.secretsecret')
    expect(issueEdgeToken).toHaveBeenCalledWith({ name: '4라인', ttl_minutes: 60 }, '1234')
    expect((screen.getByPlaceholderText('예: 3라인 젯슨') as HTMLInputElement).value).toBe('') // 다음 발급을 위해 비운다
  })

  it('refuses an out-of-range TTL before calling the server', () => {
    render(<EdgeNodesPanel scenarios={scenarios} runWithPin={runWithPin} />)
    fireEvent.change(screen.getByRole('spinbutton'), { target: { value: '5000' } })
    expect(screen.getByText('유효 시간은 1~1440분이어야 합니다.')).toBeTruthy()
    expect((screen.getByRole('button', { name: '토큰 발급' }) as HTMLButtonElement).disabled).toBe(true)
  })

  it('assigns scenarios as a full replacement, only enabled when the selection changed, then refreshes', async () => {
    listEdgeNodes.mockResolvedValue([node()])
    setEdgeNodeScenarios.mockResolvedValue(node({ scenarios: ['line1', 'line2'] }))
    render(<EdgeNodesPanel scenarios={scenarios} runWithPin={runWithPin} />)
    fireEvent.click(screen.getByRole('button', { name: '노드 불러오기' }))
    await screen.findByRole('listitem', { name: '노드 3라인 젯슨' })

    fireEvent.click(screen.getByRole('button', { name: '배정 편집' }))
    const save = screen.getByRole('button', { name: '배정 저장' }) as HTMLButtonElement
    expect(save.disabled).toBe(true) // 바뀐 게 없다
    fireEvent.click(screen.getByLabelText('2라인'))
    expect(save.disabled).toBe(false)
    fireEvent.click(save)

    await waitFor(() => expect(setEdgeNodeScenarios).toHaveBeenCalledWith('node_aaaa', ['line1', 'line2'], '1234'))
    await waitFor(() => expect(listEdgeNodes).toHaveBeenCalledTimes(2)) // 저장 뒤 다시 읽는다
  })

  it('revokes only after a second, explicit confirmation', async () => {
    listEdgeNodes.mockResolvedValue([node()])
    revokeEdgeNode.mockResolvedValue(node({ revoked_at: new Date().toISOString() }))
    render(<EdgeNodesPanel scenarios={scenarios} runWithPin={runWithPin} />)
    fireEvent.click(screen.getByRole('button', { name: '노드 불러오기' }))
    await screen.findByRole('listitem', { name: '노드 3라인 젯슨' })

    fireEvent.click(screen.getByRole('button', { name: '회수' }))
    expect(revokeEdgeNode).not.toHaveBeenCalled() // 첫 클릭은 확인을 띄울 뿐
    fireEvent.click(screen.getByRole('button', { name: '회수 확정 — 되돌릴 수 없음' }))

    await waitFor(() => expect(revokeEdgeNode).toHaveBeenCalledWith('node_aaaa', '1234'))
  })

  it('a wrong PIN is shown as such and nothing is listed', async () => {
    runWithPin.mockResolvedValue('unauthorized')
    render(<EdgeNodesPanel scenarios={scenarios} runWithPin={runWithPin} />)
    fireEvent.click(screen.getByRole('button', { name: '노드 불러오기' }))
    expect(await screen.findByText('PIN 이 올바르지 않습니다.')).toBeTruthy()
    expect(listEdgeNodes).not.toHaveBeenCalled()
  })

  it('hides the edit and revoke controls for a revoked node', async () => {
    listEdgeNodes.mockResolvedValue([node({ revoked_at: new Date().toISOString() })])
    render(<EdgeNodesPanel scenarios={scenarios} runWithPin={runWithPin} />)
    fireEvent.click(screen.getByRole('button', { name: '노드 불러오기' }))
    await screen.findByText(/회수됨/)
    expect(screen.queryByRole('button', { name: '배정 편집' })).toBeNull()
    expect(screen.queryByRole('button', { name: '회수' })).toBeNull()
  })
})
