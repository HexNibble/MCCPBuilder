# Third-Party Services and Components

## Minecraft 统一通行证 / Nide8Auth

MCCPBuilder provides optional compatibility with Minecraft 统一通行证
(Nide8Auth). MCCPBuilder is not an official client of that service and is not
affiliated with, sponsored by, endorsed by, or acting on behalf of its
operator.

When a packager enables this provider, the generated launcher sends
authentication requests directly to the HTTPS service configured by that
packager. Passwords are used only for the current authentication request and
are not written to MCCPBuilder project files, payloads, logs, or saved-login
storage. The launcher may save revocable session tokens so that it can validate
or refresh an existing session.

`nide8auth.jar` is a third-party authentication component. It is not included
in the MCCPBuilder source repository and is not licensed under MCCPBuilder's
GPL-3.0-or-later license. Packagers are responsible for obtaining the component
from an authorized source and ensuring that their use and redistribution comply
with the component owner's terms.

Minecraft 统一通行证, Nide8Auth, Minecraft, Microsoft, and related names,
logos, and components remain the property of their respective owners.

Integration documentation:
<https://login.mc-user.com:233/index/doc>

## Modrinth and CurseForge

MCCPBuilder can import a packager-selected Modrinth or CurseForge modpack.
These services, their APIs, CDN files, names, and trademarks are not part of
MCCPBuilder and are not covered by MCCPBuilder's GPL-3.0-or-later license.
Downloads go directly to the HTTPS URLs supplied by the selected service.
Packagers remain responsible for each mod, resource pack, shader pack, and
modpack license. A CurseForge API key is used only for the current build and is
not persisted in the project or generated launcher.
