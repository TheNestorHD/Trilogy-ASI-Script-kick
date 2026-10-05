#pragma once

// The same ASI source is compiled into the III, Vice City and San Andreas
// targets. Exporting directly avoids coupling the shared entrypoint to the
// San Andreas-only generated export header.
#if defined(_WIN32) && defined(_MSC_VER)
#define TRILOGYCHAOSMOD_API __declspec(dllexport)
#else
#define TRILOGYCHAOSMOD_API
#endif
