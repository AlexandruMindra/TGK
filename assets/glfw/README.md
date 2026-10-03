# GLFW for Linux

`libglfw.so.3.3` is GLFW 3.3.0 (X11), the same binary Blossom vendors in its repository (`glfw/`) and TGK shipped
up to 0.2.2. Silk.NET loads GLFW on Linux only under the name `libglfw.so.3.3`, while its native
package ships `libglfw.so.3` (GLFW 3.4), and the Blossom NuGet package carries no native files, so the client copies
this file next to its executable. GLFW is licensed under the [zlib license](https://www.glfw.org/license.html).
