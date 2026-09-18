"""Verified resume entry point. Preserve the original trainer binding and elapsed budgets."""
import argparse,ctypes,json,os
from pathlib import Path
from prepare import sha

def process_alive(pid):
    if not isinstance(pid,int) or pid<=0:return False
    if os.name=='nt':
        from ctypes import wintypes
        kernel=ctypes.WinDLL('kernel32',use_last_error=True)
        kernel.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD];kernel.OpenProcess.restype=wintypes.HANDLE
        kernel.GetExitCodeProcess.argtypes=[wintypes.HANDLE,ctypes.POINTER(wintypes.DWORD)]
        kernel.CloseHandle.argtypes=[wintypes.HANDLE]
        handle=kernel.OpenProcess(0x1000,False,pid)
        if not handle:
            error=ctypes.get_last_error()
            if error==5:raise RuntimeError('Cannot inspect the recorded training process; do not start a duplicate trainer')
            return False
        try:
            code=wintypes.DWORD()
            if not kernel.GetExitCodeProcess(handle,ctypes.byref(code)):raise ctypes.WinError(ctypes.get_last_error())
            return code.value==259
        finally:kernel.CloseHandle(handle)
    try:os.kill(pid,0);return True
    except ProcessLookupError:return False

def verify(root,checkpoint):
    root=Path(root);checkpoint=Path(checkpoint)
    progress=json.loads((root/'pilot/progress.json').read_text(encoding='utf8'))
    if progress['status']=='COMPLETE':raise RuntimeError('The bounded pilot is complete; resuming cannot extend its budget')
    if process_alive(progress.get('processId')):raise RuntimeError('The recorded trainer is still running; a second trainer is not allowed')
    sidecar=Path(str(checkpoint)+'.json')
    if not sidecar.exists():raise RuntimeError('Only a causal checkpoint with its durable checksum/budget sidecar can resume')
    metadata=json.loads(sidecar.read_text(encoding='utf8'))
    if metadata.get('fingerprint')!=progress['fingerprint'] or sha(checkpoint)!=metadata.get('sha256'):
        raise RuntimeError('Checkpoint binding or checksum mismatch')
    return metadata

def main():
    p=argparse.ArgumentParser();p.add_argument('root');p.add_argument('checkpoint');a=p.parse_args()
    verify(a.root,a.checkpoint)
    from train import train
    train(a.root,a.checkpoint)
if __name__=='__main__':main()
