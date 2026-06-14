# Adaptive Secure Overlay - IPsec Hub

![Adaptive Secure Overlay](assets/banner.svg)

[English version](README.md) | Русская версия

Adaptive Secure Overlay IPsec Hub — это центральный репозиторий IPsec-направления проекта. Здесь собрана общая идея routed control-plane, архитектурная логика overlay и ссылки на платформенные реализации.

## Основные репозитории

- [adaptive-ipsec-overlay](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay) — IPsec core и общий трек
- [adaptive-ipsec-overlay-linux](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-linux) — Linux endpoint/intermediate
- [adaptive-ipsec-overlay-openwrt](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-openwrt) — OpenWRT endpoint/intermediate
- [adaptive-ipsec-overlay-routeros7](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-routeros7) — RouterOS 7 relay/intermediate

## Платформенные направления

- [adaptive-ipsec-overlay-android](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-android) — Android client prototype
- [adaptive-ipsec-overlay-windows](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-windows) — Windows IPsec client track
- [adaptive-wireguard-overlay](https://github.com/Adaptive-Secure-Overlay/adaptive-wireguard-overlay) — WireGuard compatibility stub

## Базовая идея

- Control-plane маршрутизируется через выбранные промежуточные узлы.
- После установления защищённой сессии data-plane идёт напрямую.
- Знание маршрута и служебных параметров по возможности разделяется между ролями.
- IPsec сейчас является основной публичной реализацией этой схемы.
