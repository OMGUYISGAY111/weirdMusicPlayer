<script setup lang="ts">
import { RouterView } from 'vue-router'
import QueuePlayer from '@/components/QueuePlayer.vue'
import { usePlaylistStore } from '@/stores/playlist'
const playlist = usePlaylistStore()
</script>

<template>
  <div class="app-shell" :class="{ 'has-queue': playlist.state.items.length }">
    <RouterView />
    <QueuePlayer />
  </div>
</template>

<style>
.app-shell.has-queue { padding-bottom: 190px; }
</style>
