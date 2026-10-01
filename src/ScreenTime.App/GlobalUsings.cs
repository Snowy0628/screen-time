// 全局 using。本项目同时使用 WPF 与 WinForms，两者的同名类型很多
// （Brush / Color / Application / Image / Orientation ...），因此**刻意不注入**
// global using System.Windows.Forms 与 System.Windows 系列，
// 由各文件显式声明它要哪一套。这里只放真正无歧义的基础命名空间。

global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading.Tasks;
