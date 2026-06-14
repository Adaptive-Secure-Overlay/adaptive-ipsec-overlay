# Adaptive Secure Overlay - IPsec Hub

![Adaptive Secure Overlay](assets/banner.svg)

[![English version](https://img.shields.io/badge/language-English-0f172a?style=flat-square)](README.md)
[![Русская версия](https://img.shields.io/badge/language-Русская%20версия-0369a1?style=flat-square)](README.ru.md)

Adaptive Secure Overlay IPsec Hub — это центральный репозиторий IPsec-направления проекта. Здесь собраны общая логика routed control-plane, архитектурная схема overlay, лабораторная база и ссылки на платформенные реализации.

## Основные репозитории

- [adaptive-ipsec-overlay](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay) — IPsec core и общий трек
- [adaptive-ipsec-overlay-linux](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-linux) — Linux endpoint/intermediate
- [adaptive-ipsec-overlay-openwrt](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-openwrt) — OpenWRT endpoint/intermediate
- [adaptive-ipsec-overlay-routeros7](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-routeros7) — RouterOS 7 relay/intermediate

## Платформенные направления

- [adaptive-ipsec-overlay-android](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-android) — Android client prototype
- [adaptive-ipsec-overlay-windows](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-windows) — Windows IPsec client track
- [adaptive-wireguard-overlay](https://github.com/Adaptive-Secure-Overlay/adaptive-wireguard-overlay) — WireGuard compatibility stub

## Что делает эта ветка

- Управляющий обмен может идти не по прямому пути, а через выбранные промежуточные узлы.
- После установления защищённой сессии полезный трафик может идти напрямую между конечными узлами.
- Знание маршрута и служебных параметров по возможности разделяется между ролями.
- IPsec сейчас является основной публичной реализацией этой схемы.
