import { createRouter, createWebHistory } from 'vue-router'
import LoginPage from './pages/LoginPage.vue'
import PasswordPage from './pages/PasswordPage.vue'
import WorkspacePage from './pages/WorkspacePage.vue'
import SystemPage from './pages/SystemPage.vue'

export const router = createRouter({ history: createWebHistory(), routes: [
  { path: '/', redirect: '/workspace' }, { path: '/login', component: LoginPage },
  { path: '/password', component: PasswordPage }, { path: '/workspace', component: WorkspacePage },
  { path: '/system', component: SystemPage }, { path: '/users', redirect: '/system' },
  { path: '/site', redirect: to => ({path:'/workspace',query:to.query}) },
  { path: '/software', redirect: to => ({path:'/workspace',query:{...to.query,view:'software'}}) },
  ...['instances','enrollment','releases','deployments'].map(panel=>({path:'/'+panel,redirect:(to:{query:Record<string,unknown>})=>({path:'/workspace',query:{...to.query,view:'software',panel}})})),
  { path: '/:pathMatch(.*)*', redirect: '/workspace' },
] })
