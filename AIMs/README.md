# AIMs - the AI Modules

The AIMs Thalia runs, by standard and version (`MMC`, `OSD`, `PAF`, `HCI`), the library of their shared Data Types
(`Core`), the ONNX helper (`Onnx`), the **providers** that build each Module's AIMs (`Providers`), the **L3s** of the
Modules and AIMs (`AMDs`) and their settings (`aim-settings.json`: the models, the thresholds).

An AIM is here to be run by the Controller; it is told where its models are by `aim-settings.json`.
