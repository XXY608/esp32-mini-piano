"""Exercise the shipped interpreter, native dependencies, tools and model files."""
import importlib
import json
import os
from pathlib import Path
import subprocess
import sys

root = Path(sys.argv[1]).resolve()
helper = root / 'work/lyrics-alignment'
python = helper / 'python'
os.environ['HF_HOME'] = str(helper / 'models/huggingface')
os.environ['HF_HUB_OFFLINE'] = '1'
os.environ['MPLBACKEND'] = 'Agg'
assert Path(sys.executable).is_relative_to(python)
assert Path(sys.prefix).is_relative_to(python)
assert sys.flags.isolated
assert all(Path(p).resolve().is_relative_to(helper) for p in sys.path)
for name in ['torch', 'torchaudio', 'demucs', 'g2pflow', 'librosa', 'parselmouth',
             'torchcrepe', 'soundfile', 'yt_dlp', 'imageio_ffmpeg', 'inference']:
    module = importlib.import_module(name)
    assert Path(module.__file__).resolve().is_relative_to(helper), (name, module.__file__)
import torch
assert torch.ones((2, 2)).matmul(torch.ones((2, 2))).tolist() == [[2., 2.], [2., 2.]]
import io
import matplotlib.pyplot as plt
figure, axes = plt.subplots()
axes.plot([0, 1], [0, 1])
figure.savefig(io.BytesIO(), format='png')
plt.close(figure)
assert 'tkinter' not in sys.modules
import ctypes
kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
kernel32.GetModuleHandleW.argtypes = [ctypes.c_wchar_p]
kernel32.GetModuleHandleW.restype = ctypes.c_void_p
kernel32.GetModuleFileNameW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_uint]
for name in ['msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll']:
    handle = kernel32.GetModuleHandleW(name)
    buffer = ctypes.create_unicode_buffer(32768)
    assert handle and kernel32.GetModuleFileNameW(handle, buffer, len(buffer))
    assert Path(buffer.value).is_relative_to(python), (name, buffer.value)
import imageio_ffmpeg
ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
assert Path(ffmpeg).is_relative_to(python)
subprocess.run([ffmpeg, '-version'], stdout=subprocess.DEVNULL, check=True)
from demucs.pretrained import get_model
model = get_model('htdemucs')
assert model.sources == ['drums', 'bass', 'other', 'vocals']
assert (helper / 'models/TIFA-1.0/model.ckpt').stat().st_size > 100_000_000
assert (python / 'Lib/site-packages/torchcrepe/assets/full.pth').is_file()
print(json.dumps({'isolated': True, 'python': sys.version.split()[0], 'prefix': sys.prefix,
                  'demucs': 'loaded offline', 'ffmpeg': ffmpeg}, ensure_ascii=False))
