# FSR1 external implementation and attribution

The FSR adapter includes AMD FidelityFX Super Resolution 1 headers from the
user's installed `com.unity.render-pipelines.core` package. EASU, RCAS and the
approximate arithmetic are AMD algorithms, not this project's invention.
The headers themselves are not vendored in this repository. Unity's adaptation
is governed by the notices accompanying that installed package. The test-only
scalar oracle follows the published FSR1 math and approximate arithmetic.

Upstream: https://github.com/GPUOpen-Effects/FidelityFX-FSR

AMD's license below applies to its FSR1 implementation and derived portions,
including those compiled through the external includes. Keep this notice with
binary redistributions. It does not relicense unrelated toolkit code.

## AMD FSR1 license

Copyright (c) 2021 Advanced Micro Devices, Inc. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
