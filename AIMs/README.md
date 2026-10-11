# AIMs - the AI Modules

The AIMs of every application, by standard and version (`MMC`, `OSD`, `PAF`, `HCI` for Thalia; `CAV`, `CAE3`, `CVE` and more of `OSD`
for the Connected Autonomous Vehicle), the library of their shared Data Types (`Core`), the ONNX helper (`Onnx`), the **providers**
that build Thalia's Modules (`Providers`), the **L3s** of the Modules and AIMs (`AMDs`) and their settings (`aim-settings.json`:
the models, the thresholds).

An AIM is common to all applications: 17 are used by both Thalia and the CAV. An AIM is here to be run by the Controller; it is told
where its models are by `aim-settings.json`. See the [Developer Guide](../docs/Developer-Guide.md).
