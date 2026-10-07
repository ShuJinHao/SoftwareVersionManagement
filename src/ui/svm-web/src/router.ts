import { createRouter, createWebHistory } from 'vue-router'
import LoginPage from './pages/LoginPage.vue'
import PasswordPage from './pages/PasswordPage.vue'
import UsersPage from './pages/UsersPage.vue'

export const router = createRouter({ history: createWebHistory(), routes: [
  { path: '/', redirect: '/users' }, { path: '/login', component: LoginPage },
  { path: '/password', component: PasswordPage }, { path: '/users', component: UsersPage },
  { path: '/:pathMatch(.*)*', redirect: '/users' },
] })
