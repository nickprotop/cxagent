# CXP_NAME

A cxagent plugin with one tool, `CXP_NAME_count`, which counts the lines and words in a file. It asks
before reading a file outside the working folder, and reports its progress while it reads.

Setting: `"countBlankLines": true` counts blank lines too (default: false).

## Develop

```
cmake -B build && cmake --build build
cxagent plugin check build
cxagent plugin call build CXP_NAME_count '{"file":"CMakeLists.txt"}' --answer allow
ctest --test-dir build
cxagent plugin pack build --native build/CXP_NAME.so=linux-x64
```

`.github/workflows/build.yml` builds all six platforms and, on a tag, packs them into one release.
