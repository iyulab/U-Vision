/**
 * 캡처 큐 — 업로드 전에 캡처를 기기 안(IndexedDB)에 먼저 적는다. 네트워크가 끊겨도 캡처가 사라지지 않고,
 * 연결이 돌아오면 `queueSender` 가 꺼내 보낸다. React 를 모르는 저장소 모듈이다.
 *
 * 이미지는 Blob 이 아니라 바이트(ArrayBuffer) + 형식으로 둔다 — 브라우저마다 IndexedDB 의 Blob 저장이
 * 고르지 않았던 이력이 있고, 바이트는 어디서나 같은 structured clone 이다.
 */

export interface QueuedCapture {
  id: string
  bytes: ArrayBuffer
  type: string
  scenarioId: string
  deviceId: string
  deviceLabel: string
  /** 오투입 확인일 때만 — 기대 품번. */
  expectedPart?: string
  /** 캡처 때 장치가 읽은 코드(있을 때). */
  observedCode?: string
  /** 캡처 시각(UTC ISO 8601) — 늦게 올라가도 서버 레코드에 실제 시각이 남는다. */
  capturedAt: string
}

export interface CaptureQueue {
  add(item: QueuedCapture): Promise<void>
  get(id: string): Promise<QueuedCapture | undefined>
  /** 캡처 시각 순(먼저 찍은 것부터) id 목록. */
  ids(): Promise<string[]>
  remove(id: string): Promise<void>
  count(): Promise<number>
}

const STORE = 'captures'
const BY_TIME = 'capturedAt'

function request<T>(req: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    req.onsuccess = () => resolve(req.result)
    req.onerror = () => reject(req.error)
  })
}

function done(tx: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve()
    tx.onerror = () => reject(tx.error)
    tx.onabort = () => reject(tx.error)
  })
}

/** 큐 DB 를 연다. IndexedDB 를 쓸 수 없는 환경(일부 사생활 보호 모드)에서는 거부된다 — 호출부가 즉시 전송으로 내려간다. */
export async function openCaptureQueue(name = 'u-vision-capture-queue'): Promise<CaptureQueue> {
  const open = indexedDB.open(name, 1)
  open.onupgradeneeded = () => {
    const store = open.result.createObjectStore(STORE, { keyPath: 'id' })
    store.createIndex(BY_TIME, 'capturedAt')
  }
  const db = await request(open)

  const write = async (fn: (store: IDBObjectStore) => void) => {
    const tx = db.transaction(STORE, 'readwrite')
    fn(tx.objectStore(STORE))
    await done(tx)
  }
  const read = <T>(fn: (store: IDBObjectStore) => IDBRequest<T>) =>
    request(fn(db.transaction(STORE, 'readonly').objectStore(STORE)))

  return {
    add: (item) => write((s) => s.put(item)),
    get: (id) => read((s) => s.get(id) as IDBRequest<QueuedCapture | undefined>),
    ids: async () => (await read((s) => s.index(BY_TIME).getAllKeys())).map(String),
    remove: (id) => write((s) => s.delete(id)),
    count: () => read((s) => s.count()),
  }
}
