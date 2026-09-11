# MAC — Multimodal Access Control · User Guide

MAC recognises you from your **face and your voice** and grants or denies access.
A 3-D **Speaking Avatar** guides you through the steps and speaks the verdict.
Access is granted only when the face and the voice **agree** on the same enrolled
person.

---

## What you need

- A **Windows PC** with the **.NET 10** runtime installed.
- A **webcam** and a **microphone**, both connected and not in use by another app.
- The **model files** in place (your administrator/developer installs these once —
  see the Developer guide).
- To be **enrolled**: MAC recognises people already in its gallery. If you have
  not been enrolled, MAC will correctly report that you are not identified.

> If the webcam is unplugged or held by another application (for example the
> Windows Camera app), MAC cannot capture your face and will report a failure.
> Close other camera apps and make sure the webcam is connected.

## Running MAC

1. Start **MacApp.exe** (or run `MacAppBuild.bat` once to produce it, then run it).
2. Wait a moment while the avatar and the models load.
3. Press **Start**.

## Using MAC

1. The avatar says **“Welcome … look at the camera.”** Look at the webcam.
2. MAC captures your face, then asks you to **speak your passphrase**. Speak
   naturally, then pause.
3. The avatar speaks the **verdict** and a banner shows it:
   - **Green** — access granted, with your name.
   - **Red** — not identified.
4. Press **Start** again for another attempt.

## Getting a good result

- **Light your face evenly** and look toward the camera; avoid strong backlight.
- Keep your face **reasonably close and centred** in the frame.
- Speak your passphrase **clearly**, in a quiet moment, then pause so MAC knows
  you have finished.

## If something goes wrong

| You see / hear | Likely cause | What to do |
| --- | --- | --- |
| “Startup failed …” | The app cannot find its files | Run it from inside the installed folder (see the Developer guide); do not move `MacApp.exe` out on its own. |
| Immediate “not identified”, no verdict spoken | No face was captured | Check the webcam is connected and not used by another app; improve lighting; try again. |
| “Not identified” for you specifically | You are not enrolled, or conditions differ from enrolment | Ask to be enrolled; retry in good light, facing the camera. |
| The avatar does not speak | Audio device or missing voice model | Check the speakers and that the voice model is installed. |

## Privacy

MAC compares your face and voice against a local **enrolment gallery** held on the
machine. The gallery is user data; it is not part of the software distribution.
