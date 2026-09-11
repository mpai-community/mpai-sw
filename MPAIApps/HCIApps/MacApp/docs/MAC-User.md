# MAC - User Guidelines

**MAC** is the **Multimodal Access Control** application. It decides whether the
person in front of the camera and microphone is a **registered user**, using
both their **face** and their **voice**, and grants access only when both agree.

## What it is for
MAC answers *"who is this user, and should they be granted access?"* for people
already enrolled (see the companion **ACR** registration app). It is a
demonstration of anonymous-free, multimodal biometric access on the MPAI-AIF
platform, with a Speaking Avatar that guides the user.

## Before you start
- A **webcam** and **microphone** must be connected and available (not held by
  another application such as the Windows Camera app or a video-call tool).
- Good, even **lighting** on the face - face recognition is sensitive to poor or
  very dark lighting.
- At least one person must already be **registered** (via ACR).

## How to use it
1. Launch **MacApp**. The Speaking Avatar appears.
2. Press **Start**.
3. The avatar says *"Welcome to the HCI Multimodal Access Control Service. Look
   at the camera."* - look straight at the camera.
4. The avatar says *"Speak your passphrase."* - say a short phrase.
5. MAC recognises the face and the voice, reconciles them, and responds:
   - if both agree on a registered person -> **"&lt;name&gt;, welcome"** (access granted);
   - otherwise -> **"I am sorry, you have not been identified."**

## Notes & troubleshooting
- **Not recognised though you are registered:** usually **lighting**. Face the
  camera squarely in good light and try again.
- **Nothing happens / no avatar speech:** ensure no other app is using the
  camera; relaunch.
- MAC grants access only when **face and voice both point to the same person** -
  this is by design; a face-only or voice-only match is refused.
