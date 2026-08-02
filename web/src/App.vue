<script setup lang="ts">
import AppBar from "./components/AppBar.vue";
</script>

<template>
  <UApp>
    <!-- Nav Bar / App Bar -->
    <AppBar />

    <!-- Content -->
    <RouterView />
  </UApp>
</template>
