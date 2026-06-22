import { afterEach, describe, expect, it } from 'vitest'
import { getDeviceId, getDeviceLabel, setDeviceLabel } from './deviceIdentity'

afterEach(() => localStorage.clear())

describe('deviceIdentity', () => {
  it('getDeviceId는 최초 생성 후 호출 간 안정', () => {
    const id1 = getDeviceId()
    const id2 = getDeviceId()
    expect(id1).toBeTruthy()
    expect(id1).toBe(id2)
  })

  it('getDeviceId는 localStorage에 영속', () => {
    const id = getDeviceId()
    expect(localStorage.getItem('uvision.deviceId')).toBe(id)
  })

  it('라벨 set/get 라운드트립, 기본은 빈 문자열', () => {
    expect(getDeviceLabel()).toBe('')
    setDeviceLabel('라인 A 입구')
    expect(getDeviceLabel()).toBe('라인 A 입구')
  })
})
