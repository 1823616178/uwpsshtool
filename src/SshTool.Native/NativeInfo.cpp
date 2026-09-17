#include "pch.h"
#include "NativeInfo.h"

using Platform::String;
using SshTool::Native::NativeInfo;

String^ NativeInfo::Version()
{
    return "0.0.1";
}
