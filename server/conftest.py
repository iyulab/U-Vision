# pytest rootdir 마커.
# 이 파일의 존재로 pytest 가 server/ 를 sys.path 에 넣어 `import app` 이 해결된다.
# (bare `pytest` 와 `python -m pytest` 양쪽에서 동일하게 동작 — CI/로컬 일관성)
