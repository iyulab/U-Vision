import { describe, expect, it } from 'vitest'

import { formatBytes, summarizeVolume } from './dataVolume'

const GB = 1024 ** 3

describe('formatBytes', () => {
  it('단위를 올려 읽기 쉽게 쓴다', () => {
    expect(formatBytes(0)).toBe('0 B')
    expect(formatBytes(1536)).toBe('2 KB')
    expect(formatBytes(5 * 1024 ** 2)).toBe('5 MB')
    expect(formatBytes(12.34 * GB)).toBe('12.3 GB')
    expect(formatBytes(2 * 1024 ** 4)).toBe('2.0 TB')
  })

  it('음수는 0 으로', () => {
    expect(formatBytes(-5)).toBe('0 B')
  })
})

describe('summarizeVolume', () => {
  it('여유와 전체를 문구로, 문턱 미만 표시는 서버 값을 그대로', () => {
    const health = { status: 'ok', provider: 'mock', data_volume: { free_bytes: GB, total_bytes: 250 * GB, low: true } }
    expect(summarizeVolume(health)).toEqual({ text: '여유 1.0 GB / 전체 250.0 GB', low: true })
  })

  it('서버가 볼륨을 읽지 못하면 null — 모른다는 것을 여유 있음으로 보이지 않는다', () => {
    expect(summarizeVolume({ status: 'ok', provider: 'mock' })).toBeNull()
    expect(summarizeVolume(null)).toBeNull()
  })
})
