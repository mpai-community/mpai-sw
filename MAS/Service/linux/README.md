# Installing MPAI-MAS on a Linux server

MPAI as a Service - MAS-App and the six Apps it offers (MAD, AMQ, MAT, MPD, and since
Phase 16 MAC and ACR), with their AIMs and models - runs on Linux. Installed as described here, the server holds
all of it: the **MAS Service** (the Apps' Modules, their AIMs, the models, Ollama)
and the **browser client's host** (the page people open). People need only a
browser, on any machine anywhere, many at once: `https://<server>/`. (The client's
*host* is the server program that sends browsers the page and relays their
requests; the people using it are wherever their browsers are.)

Tested on 2026/09/26 by following these steps in WSL (Ubuntu 26.04, .NET 10.0.12,
x86-64) into a folder of its own: the four Apps answered as on Windows - MAD "The
capital of France is Paris.", AMQ "red", MAT "Buongiorno, come stai tu?", MPD
"That's wonderful to hear!" - and the browser client, served by the installed host,
offered the four Apps. Tested again on 2026/09/29 with the six Apps, by the author
in a browser on Windows: the seven Modules loaded, MAC and ACR included; a person
registered with ACR was admitted by MAC, and refused once the session had closed.
What was not tested is listed at the end.

## What the server holds

Everything under one folder, `/opt/mpai` here (any other works: give it to
`package.sh`):

| Folder | What | Size |
|---|---|---|
| `service/` | the MAS Service, published for linux-x64 | 29 MB |
| `client/` | the browser client's host, with the client (WebAssembly) | 64 MB |
| `Apps/`, `AMDs/`, `schemas/` | the six Apps, the L3s of the AIMs, the schemas | 2 MB |
| `UserAgent/` | the avatar and the client's workflow | 6 MB |
| `Models/` | the model files the Apps use - only those | 6.8 GB |
| `bin/` | Piper 1.2.0 (TTS), `whisper-cli` of whisper.cpp 1.9.3 (ASR) | 55 MB |
| `ollama/`, `ollama-models/` | Ollama 0.34.4, and llama3.2:3b (EDP, in MAD and MPD) | 4 GB |
| `tls/` | the certificate of the client's host | - |
| `mas-server.json`, `aim-settings.json` | the Service's configuration and the AIMs' settings | - |
| `systemd/` | the units that run it | - |

About 11 GB of disk. Memory: 16 GB is comfortable.

**A GPU is optional, but a server that has one must be set up to use it** - a cloud
image usually comes without the NVIDIA driver, and then everything runs on the CPU with
no error anywhere, only slowly. `setup.sh` says what it finds under "the GPU". With an
NVIDIA GPU:

- the **driver** (as root on Ubuntu: `apt-get install -y ubuntu-drivers-common && ubuntu-drivers install`,
  then reboot): Ollama then runs the language model on the GPU (check with
  `ollama ps`: `100% GPU`);
- the **CUDA toolkit** (`apt-get install -y nvidia-cuda-toolkit`): `setup.sh` then builds
  whisper.cpp with CUDA, and rebuilds it if it was built for the CPU (`bin/whisper.build`);
- **CUDA 13 and cuDNN 9** (ONNX Runtime 1.27 needs `libcudart.so.13`, `libcublas(Lt).so.13`,
  `libnvrtc.so.13` and `libcudnn.so.9`, and so a driver of version 580 or later; from NVIDIA's
  apt repository: `apt-get install -y cuda-libraries-13-0 libcudnn9-cuda-13`), and the package
  made with `MPAI_GPU=1 package.sh ...`: the
  ONNX models (pictures, translation, voice and face emotion, faces, speakers) then run
  on the GPU. `MPAI_ONNX_DEVICE` in `mpai-service.service` chooses: `auto` (the GPU where it
  can, the CPU otherwise - the default), `cuda` (the GPU or fail), `cpu`. The Service's
  log says, for each model, where it runs (`[ONNX] ...: CUDA`).

**Build whisper.cpp with CUDA once, not on every server.** The CUDA build of whisper.cpp
is the one heavy compile of the installation (nvcc, one large process per core): a server of
modest cores and memory cannot finish it. Piper (a ready-made binary) and Ollama (which brings
its own CUDA code) need no compile. So build whisper.cpp on a build machine that has the
CUDA 13 toolkit, for the GPU the servers have, and package the result:

```
MAS/Service/linux/build-whisper.sh ~/whisper-cuda 86            # 86 = A10G / RTX A-series; 89 = L4 / RTX 4000 Ada / 4080-4090
MPAI_GPU=1 MPAI_WHISPER_DIR=~/whisper-cuda MAS/Service/linux/package.sh ~/mpai-package /opt/mpai
```

`setup.sh` then does not build: it uses the packaged `whisper-cli` and `whisper-server`
(`bin/whisper.build` says `cuda-prebuilt`), needs no `g++`, `cmake` or `nvcc`, and reports
any CUDA library the server lacks (`apt-get install -y cuda-libraries-13-0`, the same
libraries ONNX Runtime needs). The build is for the architectures given: a server whose GPU
is of another architecture needs its own build.

**The first answer.** Before it listens, the Service answers a few turns of each App
(the files in `warmup/`), so that loading the language model, starting each voice and
the first run of each model happen then and not at the first person's first question.
The log shows each turn and its time ("warm-up ..."). `"WarmUp": false` in
`mas-server.json` turns it off.

The Service listens on the server's loopback only (`127.0.0.1:5005`): the network
reaches the client's host, over HTTPS, and the host passes the client's requests on.
No bearer token is then needed. The Service can also be on a machine of its own,
reached by the client's host or by desktop clients: see "Other layouts" below.

## Ubuntu 24.04 with an NVIDIA GPU: the repositories

.NET 10 is in Ubuntu 24.04's own feed (`apt-get install -y aspnetcore-runtime-10.0`, as in
step 2): nothing to add. The CUDA libraries (and the toolkit, on a machine that builds
whisper.cpp) come from NVIDIA's repository, which must be added first, as root:

```
apt-get install -y wget
wget https://developer.download.nvidia.com/compute/cuda/repos/ubuntu2404/x86_64/cuda-keyring_1.1-1_all.deb
dpkg -i cuda-keyring_1.1-1_all.deb && apt-get update
apt-get install -y cuda-libraries-13-0            # a server: the libraries only
apt-get install -y cuda-toolkit-13-0              # the build machine of build-whisper.sh: nvcc too
apt-cache search cudnn9                           # the cuDNN 9 package for CUDA 13 (libcudnn9-cuda-13 or cudnn9-cuda-13)
```

The NVIDIA driver needs version 580 or later (`ubuntu-drivers install`, then reboot).
Checked on paper against the repository listings only: not yet run on a 24.04 server.

**The published package** (`mpai-linux-ubuntu2404-*.tar.gz`, no model files) was made on
Windows, so after unpacking it, as the service user, in the folder it unpacked to:

```
tar xzf mpai-linux-ubuntu2404-cuda12-*.tar.gz --no-same-owner    # the archive carries a Windows user id a container cannot give
chmod +x setup.sh build-whisper.sh run.sh service/MasService client/RcaWeb.Host
```

and copy the repository's `Models` folder (6.8 GB, the files `aim-settings.json` names) to
`/opt/mpai/Models`. Then continue with step 2 (the files are already in place).

## In a container without systemd (a RunPod pod), and a driver older than 580

**CUDA 12 build.** ONNX Runtime 1.27 needs CUDA 13 and so driver 580 or later. For a host
with an older driver (550, CUDA 12.4), the package is made with `MPAI_CUDA=12`: ONNX
Runtime 1.26.0, the CUDA 12.8 build, which runs on a 12.x driver by CUDA's minor-version
compatibility (a GeForce card such as the RTX 4090 has no other way: the forward-compatibility
packages exist for data-centre GPUs only). `mpai-linux-ubuntu2404-cuda12-*.tar.gz` is that package;
`cuda.version` in it says 12 and `setup.sh` then checks for the CUDA 12 libraries. In RunPod's
pod filter, also choose hosts with CUDA 12.8 or later if there is a choice. Not yet run on a
driver 550 host: the first run decides.

```
apt-get install -y cuda-libraries-12-8 libcudnn9-cuda-12     # from NVIDIA's repository (above); the driver is the host's
apt-get install -y cuda-toolkit-12-8                          # only where whisper.cpp is built: the SAME 12.8 as the libraries - two CUDA 12 versions side by side leave the loader's choice to chance
build-whisper.sh ~/whisper-cuda 89                            # 89: RTX 4090, L4
cp ~/whisper-cuda/bin/* /opt/mpai/bin/                        # before setup.sh: it then builds nothing
```

**What a real pod showed** (RunPod, Ubuntu 24.04.3, NVIDIA RTX PRO 4500 Blackwell, driver 580,
tried 2026-10-07): the image already had the CUDA 12.8 compiler, libraries and cuDNN 9 for CUDA 12
(`dpkg -l | grep cuda`; some of them held), and NVIDIA's apt repository. So on such an image
install no CUDA at all - and do **not** add `cuda-keyring`: a second copy of the repository
makes apt fail ("Conflicting values set for option Signed-By"). Use the **cuda12** package there
(CUDA 13 libraries cannot be installed beside the held 12.8 ones). `nproc` says 128 on that pod
but the container may use 13.6 CPUs and 87 GB (cgroup limits): `build-whisper.sh` reads them.
A Blackwell GPU (RTX PRO, RTX 50xx) is architecture **120**: `build-whisper.sh ~/whisper-cuda 120`,
which took about 3 minutes with 14 jobs.

**No systemd.** A container has none, so `setup.sh`'s last step is not the units but

```
./run.sh start      # Ollama, the MAS Service (warm-up first), the client's host; ./run.sh status | logs | stop
```

as the user that owns the folder (root, in a pod). The client's host listens on plain HTTP,
port **8880**: add 8880 as an HTTP port of the pod and open `https://<pod-id>-8880.proxy.runpod.net/`
- RunPod's proxy does the https, which browsers need for the microphone. `./run.sh status`
shows where each model runs (`[ONNX] ...: CUDA`, `ollama ps`) and the warm-up times.
A pod's disk outside `/workspace` (or a network volume) is lost when the pod is stopped and started again
- the `/opt/mpai` link, the packages installed with apt, and the running programs: after a restart the
services are **not running** until `/workspace/mpai/pod-boot.sh` has been run (it links, reinstalls
what apt lost, and runs `run.sh start`; safe to run at any time, and usable as the pod's start command).
Keep the install on the volume (`/workspace/mpai`, linked as `/opt/mpai`) so that the rest survives.

## 1. On the build machine: the package

With the repository, its `Models` folder and the .NET 10 SDK (Git Bash on Windows, or
Linux), from the repository root:

```
MAS/Service/linux/package.sh ~/mpai-package /opt/mpai
```

It publishes the Service and the client's host for linux-x64, and copies the Apps,
the L3s, the schemas, the avatar and - from `Models` - the files `aim-settings.json`
names, into `~/mpai-package`, laid out for `/opt/mpai`.

**To send it** where the server is not reached directly - as a release, say - cut it
into parts of at most 1,900 MB (a GitHub release takes files up to 2 GB), in Linux or
WSL:

```
MAS/Service/linux/dist.sh ~/mpai-package ~/mpai-dist /opt/mpai
```

`dist.sh` takes a packaged folder or one already installed and set up (then Piper,
whisper-cli, Ollama and llama3.2:3b travel with it, and `setup.sh` has nothing to
download). It leaves out logs, downloaded sources, certificates and their keys and
the gallery, and sets the paths for `/opt/mpai`. On the server, in the folder with
the parts, as a user who may use `sudo`:

```
sha256sum -c SHA256SUMS
cat thalia-linux.tar.part* | sudo tar xf - -C /opt
```

then continue with step 2 (the files are already in `/opt/mpai`).

## 2. On the server: the system, and the files

Ubuntu 26.04 (as tested; on another release or distribution, install the ASP.NET Core 10 runtime as Microsoft documents it), as root:

```
apt-get install -y aspnetcore-runtime-10.0 espeak-ng zstd g++ cmake curl openssl
useradd --system --create-home --home-dir /opt/mpai mpai
```

`g++` and `cmake` build whisper.cpp; `espeak-ng` gives GFD the phonemes of the face;
`zstd` unpacks Ollama. Then copy the package (from the build machine):

```
rsync -a ~/mpai-package/ <server>:/opt/mpai/
```

and, on the server, make it the service user's: `chown -R mpai:mpai /opt/mpai`.

## 3. On the server: the programs

As the service user, in `/opt/mpai`:

```
sudo -u mpai /opt/mpai/setup.sh
```

It downloads Piper (26 MB), the whisper.cpp source (9 MB) - built here into one
file, `bin/whisper-cli` - and Ollama (1.4 GB), pulls llama3.2:3b (2 GB), writes the
SHA-256 of `whisper-cli` into `aim-settings.json`, and makes a self-signed certificate
in `tls/`. Anything already in place is left as it is; run it again after a failure.

## 4. The certificate

Browsers warn about the self-signed certificate. For people to use the server, put
in its place one issued for the server's name - `tls/cert.pem` and `tls/key.pem`, PEM,
the key unencrypted and readable by `mpai` only.

## 5. The services

As root:

```
cp /opt/mpai/systemd/*.service /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now mpai-ollama mpai-service mpai-client
```

- `mpai-ollama`: Ollama, on `127.0.0.1:11434`;
- `mpai-service`: the MAS Service, on `127.0.0.1:5005` - it loads the models first,
  up to a minute;
- `mpai-client`: the client's host, on `https://0.0.0.0:443` (the unit gives it the
  right to bind 443 without root).

Open port 443 in the server's firewall. Logs: `journalctl -u mpai-service` (and the
others).

## 6. Checking

On the server:

```
curl -s http://127.0.0.1:5005/MPAI/AIFU/Apps        # the Service: the six Apps
curl -sk https://localhost/MPAI/AIFU/Apps           # the same, through the client's host
```

Then from a browser: `https://<server>/`, Start, choose an App. The browser asks for
the microphone; AMQ also asks for a picture or the camera.

## The two files

`mas-server.json` - the Service:

| Setting | Here |
|---|---|
| `ListenUrl` | `http://127.0.0.1:5005/` - loopback. A Service reachable beyond loopback does not start without a `BearerToken`. |
| `AppDirectory`, `Apps` | the six Apps |
| `Gallery` | `/opt/mpai/gallery` - where Access Registration (ACR) registers persons and Access Control (MAC) recognises them; empty at installation |
| `ForgetOnClose` | `true` - what a session registers (the descriptors of a face and a voice, and a name) is deleted when the session closes: the page is closed, or silent for 90 s. ACR's avatar tells the person so |
| `AmdDirectory` | the L3s |
| `SettingsPath` | `aim-settings.json` |
| `SchemaDirectory` | the schemas: without it, a Service installed outside the repository validates nothing |

`aim-settings.json` - the AIMs: every path absolute, with `/`; `OllamaUrl` of
`1MMC-EDP-V2.5-I01` where Ollama is (`http://127.0.0.1:11434`); `SHA256:ExecutablePath`
of `1MMC-ASR-V2.5-I01` written by `setup.sh`.

## Updating

Run `package.sh` again on the build machine, copy `service/` and `client/` (and
whatever else changed) to the server, and `systemctl restart mpai-service mpai-client`.

## Other layouts

**The front end and the models on separate machines.** The MAS Service (with the
models and Ollama) on one machine, the client's host on another - the one people
reach. On the Service's machine, in `mas-server.json`:

```
"ListenUrl": "http://0.0.0.0:5005/",
"BearerToken": "<a long random string, e.g. openssl rand -hex 24>"
```

A Service that listens beyond loopback does not start without a token, and refuses
every request without it (401). Let only the front end reach port 5005 (firewall),
or give the Service a certificate (`CertificatePath`, `PrivateKeyPath`) and an
`https` address: the token travels in each request. The client's host accepts the
certificate of a Service on its own loopback as it is; a Service on another machine
must present one the front end trusts (for a self-signed one, add it to the front
end's trusted certificates). On the front end, install `client/` and
`UserAgent/` only, and give the host the Service's address and token: in
`mpai-client.service`, `--Service http://<service machine>:5005/`, and in
`/opt/mpai/service-token.env` (readable by `mpai` only):

```
MPAI_MAS_TOKEN=<the same string>
```

**Desktop clients.** The Windows desktop client reaches a Service directly: on each
PC, set `MPAI_MAS_SERVER=https://<service machine>:5005/` and
`MPAI_MAS_TOKEN=<the token>`, with the Service listening beyond loopback as above.

**Ollama elsewhere**: set `OllamaUrl`, and leave out `mpai-ollama`.

**In WSL** (as tested): the same, in a folder of the WSL user; WSL forwards the ports
its programs listen on to Windows' `localhost`. A Windows Ollama is not reachable from
WSL's default networking (and mirrored networking, which would share it, needs
Windows 11): Ollama runs in WSL, on another port than a Windows one (e.g. 11435, in
`OllamaUrl`).

The client's host may be started from any folder: published, it takes its own folder
as its content root, and runs in Production unless `ASPNETCORE_ENVIRONMENT` says
otherwise.

## Tested, and not tested

Tested in WSL, beyond the steps above: the two-machine layout, with the Service
reached at WSL's network address (not loopback) and its token. The Service refused a
request without the token (401), a client's host without it got 401, and through the
host with it the four Apps answered.

Not tested:

- two real machines, and the Service over https;
- desktop clients reaching a Service on another machine;
- the systemd units running as services: they were checked with `systemd-analyze
  verify`, and the three programs run with exactly their command lines - but not
  under systemd, nor on port 443 (5443 was used), nor as a `mpai` user;
- a server of its own, a certificate browsers trust, and a firewall;
- other distributions than Ubuntu, and ARM.
