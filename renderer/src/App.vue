<script setup lang="ts">
import { RouterView } from 'vue-router'
import QueuePlayer from '@/components/QueuePlayer.vue'
import SettingsDialog from '@/components/SettingsDialog.vue'
import { usePlaylistStore } from '@/stores/playlist'
import { useSettingsStore } from '@/stores/settings'

const playlist = usePlaylistStore()
// 设置按钮在任意页面都能打开同一个对话框，对话框只挂一次
const settings = useSettingsStore()
</script>

<template>
  <div class="app-shell" :class="{ 'has-queue': playlist.state.items.length }">
    <RouterView />
    <QueuePlayer />
    <SettingsDialog v-if="settings.open" />
  </div>
</template>

<style>
.app-shell.has-queue { padding-bottom: 190px; }
</style>
