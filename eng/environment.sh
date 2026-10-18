#!/usr/bin/env bash
SVM_ROOT="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
case "$(uname -s)" in Darwin) svm_os=darwin ;; Linux) svm_os=linux ;; *) echo "Use the native PowerShell entry on Windows." >&2; return 1 ;; esac
case "$(uname -m)" in arm64|aarch64) svm_cpu=arm64 ;; x86_64|amd64) svm_cpu=x64 ;; *) echo "Unsupported CPU." >&2; return 1 ;; esac
svm_node_version="$(sed -n 's/^  "node": "\([^"]*\)",/\1/p' "$SVM_ROOT/build/toolchain.lock.json")"
SVM_NODE="$SVM_ROOT/.tools/$svm_os-$svm_cpu/node/$svm_node_version/bin/node"
export SVM_ROOT SVM_NODE
