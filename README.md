# Adaptive Secure Overlay - IPsec Track Hub

![Adaptive Secure Overlay](assets/banner.svg)

[![English](https://img.shields.io/badge/language-English-0f172a?style=flat-square)](README.md)
[![Русская версия](https://img.shields.io/badge/language-Русская%20версия-0369a1?style=flat-square)](README.ru.md)

[![Track](https://img.shields.io/badge/track-ipsec%20track-0f172a?style=for-the-badge)](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay)
[![Status](https://img.shields.io/badge/status-research%20prototype-15803d?style=for-the-badge)](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay)
[![Scope](https://img.shields.io/badge/architecture-routed%20control--plane-0369a1?style=for-the-badge)](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay)

This repository is the IPsec hub of the project.

It is the main public entry point for the current implementation branch: the control-plane logic, lab topology, platform packages and supporting notes all meet here.

## Pinned repositories

<table>
  <tr>
    <td>
      <a href="https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay">
        <img src="https://github-readme-stats.vercel.app/api/pin/?username=Adaptive-Secure-Overlay&repo=adaptive-ipsec-overlay&theme=transparent&show_owner=true&hide_border=true" />
      </a>
    </td>
    <td>
      <a href="https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-linux">
        <img src="https://github-readme-stats.vercel.app/api/pin/?username=Adaptive-Secure-Overlay&repo=adaptive-ipsec-overlay-linux&theme=transparent&show_owner=true&hide_border=true" />
      </a>
    </td>
  </tr>
  <tr>
    <td>
      <a href="https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-openwrt">
        <img src="https://github-readme-stats.vercel.app/api/pin/?username=Adaptive-Secure-Overlay&repo=adaptive-ipsec-overlay-openwrt&theme=transparent&show_owner=true&hide_border=true" />
      </a>
    </td>
    <td>
      <a href="https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-routeros7">
        <img src="https://github-readme-stats.vercel.app/api/pin/?username=Adaptive-Secure-Overlay&repo=adaptive-ipsec-overlay-routeros7&theme=transparent&show_owner=true&hide_border=true" />
      </a>
    </td>
  </tr>
</table>

## Platform tracks

- [adaptive-ipsec-overlay-android](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-android) — Android client prototype
- [adaptive-ipsec-overlay-windows](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-windows) — native Windows client track (planned)
- [adaptive-wireguard-overlay](https://github.com/Adaptive-Secure-Overlay/adaptive-wireguard-overlay) — protocol-compatibility experiments (planned)

<div align="right">

![Top Languages](https://github-readme-stats.vercel.app/api/top-langs/?username=Adaptive-Secure-Overlay&layout=compact&hide_border=true&langs_count=8&theme=transparent)

</div>

## What this branch does

- **The setup path is flexible.** IKE or IKE-like exchange can be relayed through selected intermediate nodes instead of one obvious direct path.
- **Protected traffic stays simple.** Once the session is agreed, data traffic can go directly between endpoints.
- **Route knowledge is limited by role.** Different relay nodes may know different parts of the path.
- **The implementation stays grounded.** Linux, OpenWRT and RouterOS 7 are the current public IPsec baseline.

## Quick start

```bash
# 1) choose repo and clone
# 2) copy sample config to overlay.json / overlay.sample.json
# 3) install with package-specific installer
# 4) start daemon/service and check logs
```

## Install

- Install each package using its local installer script and README guide:
  - [adaptive-ipsec-overlay-linux](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-linux)
  - [adaptive-ipsec-overlay-openwrt](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-openwrt)
  - [adaptive-ipsec-overlay-routeros7](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-routeros7)

## Security note

Current sample configuration uses shared lab secrets. Never publish real network
credentials.
